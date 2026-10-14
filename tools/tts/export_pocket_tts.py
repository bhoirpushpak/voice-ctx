"""Export a Kyutai Pocket TTS model (pocket-tts 3.3.0) to the five ONNX graphs Voice CTX runs.

Graphs (same layout as PocketTTS.cpp, MIT, https://github.com/VolgaGerm/PocketTTS.cpp, whose
exporter this follows; rewritten here for the pocket-tts 3.x module layout):

  text_conditioner.onnx   token_ids [1,T] int64             -> embeddings [1,T,1024]
  mimi_encoder.onnx       audio [1,1,N] (24 kHz)              -> conditioning [1,F,1024]
  flow_lm_main.onnx       sequence [1,S,32], text_embeddings [1,T,1024], state_0..   -> conditioning [1024], eos_logit [1]
                          (per layer: K cache [1,1000,16,64] fp16, V cache, step [1] int64)
  flow_lm_flow.onnx       c [1024], s [1,1], t [1,1], x [1,32] -> flow_dir [1,32]
  mimi_decoder.onnx       latent [1,F,32], state_0..          -> audio_frame [1,1,F*1920]

Then dynamic int8: flow head and decoder MatMuls, and only the MLP MatMuls of the backbone
(quantizing attention too causes audible artifacts).
"""

from __future__ import annotations

import warnings
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

warnings.filterwarnings("ignore")

from pocket_tts.models.tts_model import TTSModel  # noqa: E402
from pocket_tts.modules import attention as attention_module  # noqa: E402
from pocket_tts.modules.conv import StreamingConv1d, StreamingConvTranspose1d  # noqa: E402
from pocket_tts.modules.stateful_module import init_states  # noqa: E402

MAX_SEQ = 1000      # backbone KV capacity: voice (<= 20 s = 251 frames) + text (<= 50) + speech
MIMI_CACHE = 250    # Mimi decoder transformer context (circular buffer)
OPSET = 17


def _export(module: nn.Module, args: tuple, path: Path, inputs: list[str], outputs: list[str], dynamic: dict):
    module.eval()
    with torch.no_grad():
        torch.onnx.export(module, args, str(path), input_names=inputs, output_names=outputs,
                          dynamic_axes=dynamic, opset_version=OPSET, do_constant_folding=True, dynamo=False)


# ---------------------------------------------------------------- text conditioner

class TextConditioner(nn.Module):
    def __init__(self, model: TTSModel):
        super().__init__()
        self.embed = model.flow_lm.conditioner.embed

    def forward(self, token_ids):
        return self.embed(token_ids)


# ---------------------------------------------------------------- Mimi encoder + speaker projection

class MimiEncoder(nn.Module):
    def __init__(self, model: TTSModel):
        super().__init__()
        self.mimi = model.mimi
        self.speaker_proj_weight = model.flow_lm.speaker_proj_weight

    def forward(self, audio):
        frame = self.mimi.frame_size
        audio = F.pad(audio, (0, (frame - audio.shape[-1] % frame) % frame))
        emb = self.mimi.encoder(audio, model_state=None)
        (emb,) = self.mimi.encoder_transformer(emb, model_state=None)
        emb = self.mimi._to_framerate(emb)
        return F.linear(emb.transpose(-1, -2).float(), self.speaker_proj_weight)


# ---------------------------------------------------------------- FlowLM backbone

class FlowLmMain(nn.Module):
    """FlowLM backbone with an explicit fp16 KV cache per layer (no in-place state)."""

    def __init__(self, model: TTSModel):
        super().__init__()
        lm = model.flow_lm
        self.bos_emb = lm.bos_emb
        self.input_linear = lm.input_linear
        self.out_norm = lm.out_norm
        self.out_eos = lm.out_eos
        self.layers = lm.transformer.layers

    @staticmethod
    def _attention(attn, x, cache_k, cache_v, step):
        b, length, _ = x.shape
        h, d = attn.num_heads, attn.dim_per_head
        q, k, v = torch.unbind(attn.in_proj(x).view(b, length, 3, h, d), dim=2)
        offset = step[0]
        q, k = attn.rope(q, k, offset=offset)

        positions = offset + torch.arange(length, dtype=torch.long)
        index = positions.view(1, length, 1, 1).expand(b, length, h, d)
        cache_k = cache_k.scatter(1, index, k.half())
        cache_v = cache_v.scatter(1, index, v.half())
        valid = offset + length
        keys = cache_k[:, :valid].float().transpose(1, 2)
        values = cache_v[:, :valid].float().transpose(1, 2)
        mask = positions.view(-1, 1) >= torch.arange(valid).view(1, -1)
        out = F.scaled_dot_product_attention(q.transpose(1, 2), keys, values, mask)
        out = attn.out_proj(out.transpose(1, 2).reshape(b, length, h * d))
        return out, cache_k, cache_v, step + length

    def forward(self, sequence, text_embeddings, *states):
        sequence = torch.where(torch.isnan(sequence), self.bos_emb, sequence)
        x = torch.cat([text_embeddings, self.input_linear(sequence)], dim=1)
        new_states = []
        for i, layer in enumerate(self.layers):
            attn_out, k, v, step = self._attention(layer.self_attn, layer.norm1(x), *states[3 * i: 3 * i + 3])
            x = x + layer.layer_scale_1(attn_out)
            x = layer._ff_block(x)          # pocket-tts 3.x: tanh-approximated GELU
            new_states += [k, v, step]
        x = self.out_norm(x)
        conditioning = x[:, -1]
        return (conditioning.squeeze(0), self.out_eos(conditioning).squeeze(0), *new_states)


# ---------------------------------------------------------------- flow head

class FlowHead(nn.Module):
    def __init__(self, model: TTSModel):
        super().__init__()
        self.flow_net = model.flow_lm.flow_net

    def forward(self, c, s, t, x):
        return self.flow_net(c.unsqueeze(0), s, t, x)


def _fix_identity_initializers(path: Path):
    """TorchScript export can alias equal initializers through Identity nodes ORT rejects."""
    import onnx
    from onnx import numpy_helper

    model = onnx.load(str(path))
    inits = {i.name: i for i in model.graph.initializer}
    remove = [n for n in model.graph.node if n.op_type == "Identity" and n.input[0] in inits and n.output[0] not in inits]
    for node in remove:
        model.graph.initializer.append(numpy_helper.from_array(numpy_helper.to_array(inits[node.input[0]]).copy(), node.output[0]))
        model.graph.node.remove(node)
    if remove:
        onnx.save(model, str(path))


# ---------------------------------------------------------------- Mimi decoder

def _patch_streaming_for_onnx():
    """Functional (non in-place) streaming state updates, and a circular KV cache for Mimi."""

    def conv_forward(self, x, model_state):
        state = self.init_state(x.shape[0], 0) if model_state is None else self.get_state(model_state)
        tp = state["previous"].shape[-1]
        if tp and self.pad_mode == "replicate":
            state["previous"] = torch.where(state["first"].view(-1, 1, 1), x[..., :1].expand_as(state["previous"]), state["previous"])
        if tp:
            x = torch.cat([state["previous"], x], dim=-1)
        y = self.conv(x)
        if tp:
            state["previous"] = x[..., -tp:].contiguous()
            if self.pad_mode == "replicate":
                state["first"] = torch.zeros_like(state["first"])
        return y

    def convtr_forward(self, x, mimi_state):
        state = self.get_state(mimi_state)
        partial = state["partial"]
        y = self.convtr(x)
        pt = partial.shape[-1]
        if pt > 0:
            head = y[..., :pt] + partial
            tail = y[..., -pt:]
            if self.convtr.bias is not None:
                tail = tail - self.convtr.bias[:, None]
            state["partial"] = tail
            y = torch.cat([head, y[..., pt:-pt]], dim=-1)
        return y

    backend = attention_module._LinearKVCacheBackend

    def init_state(self, batch_size, sequence_length, device, dtype):
        return dict(offset=torch.zeros(batch_size, dtype=torch.long, device=device),
                    cache_k=torch.zeros(batch_size, sequence_length, self.num_heads, self.dim_per_head, dtype=torch.float16),
                    cache_v=torch.zeros(batch_size, sequence_length, self.num_heads, self.dim_per_head, dtype=torch.float16))

    def append_and_get(self, k, v, state):
        if state is None:
            b = k.shape[0]
            pos = torch.arange(k.shape[1], dtype=torch.long).view(1, -1).expand(b, -1)
            return k.permute(0, 2, 1, 3), v.permute(0, 2, 1, 3), pos, torch.zeros(b, dtype=torch.long)
        cache_k, cache_v, offset = state["cache_k"], state["cache_v"], state["offset"]
        off = offset[0]
        b, length, h, d = k.shape
        capacity = cache_k.shape[1]
        write = (off + torch.arange(length, dtype=torch.long)) % capacity
        index = write.view(1, length, 1, 1).expand(b, length, h, d)
        cache_k = cache_k.scatter(1, index, k.half())
        cache_v = cache_v.scatter(1, index, v.half())
        state["cache_k"], state["cache_v"] = cache_k, cache_v
        last = off + length - 1
        delta = torch.arange(capacity, dtype=torch.long) - last % capacity
        absolute = torch.where(delta <= 0, last + delta, last + delta - capacity)
        pos_k = torch.where(absolute >= 0, absolute, torch.full_like(absolute, -1)).view(1, -1).expand(b, -1)
        return cache_k.float().permute(0, 2, 1, 3), cache_v.float().permute(0, 2, 1, 3), pos_k, offset

    def increment_step(self, state, increment):
        state["offset"] = state["offset"] + increment

    StreamingConv1d.forward = conv_forward
    StreamingConvTranspose1d.forward = convtr_forward
    backend.init_state = init_state
    backend.append_and_get = append_and_get
    backend.increment_step = increment_step


def _state_layout(module: nn.Module):
    states = init_states(module, batch_size=1, sequence_length=MIMI_CACHE)
    return [(m, k) for m in sorted(states) for k in sorted(states[m])], states


class MimiDecoder(nn.Module):
    def __init__(self, model: TTSModel):
        super().__init__()
        self.model = model
        self.layout, _ = _state_layout(model.mimi)

    def forward(self, latent, *flat):
        state: dict = {}
        for (m, k), tensor in zip(self.layout, flat):
            state.setdefault(m, {})[k] = tensor
        lm, mimi = self.model.flow_lm, self.model.mimi
        audio = mimi.decode_from_latent(latent * lm.emb_std + lm.emb_mean, state)
        steps = latent.shape[1] * 16    # Mimi transformer runs at 16x the latent frame rate
        for m in state:
            if "offset" in state[m]:
                state[m]["offset"] = state[m]["offset"] + steps
        return (audio, *[state[m][k] for m, k in self.layout])


# ---------------------------------------------------------------- export + quantize

def export_all(model: TTSModel, out: Path, keep_fp32: bool = False):
    out.mkdir(parents=True, exist_ok=True)
    # pocket-tts caches the encoder's causal mask globally. A mask cached by an earlier export would be
    # traced as a fixed-size constant (breaking audio prompts longer than the dummy input), so start empty.
    attention_module._CAUSAL_MASK_CACHE.clear()
    _export(TextConditioner(model), (torch.tensor([[10, 20, 30]]),), out / "text_conditioner.onnx",
            ["token_ids"], ["embeddings"], {"token_ids": {1: "text_len"}, "embeddings": {1: "text_len"}})

    _export(MimiEncoder(model), (torch.randn(1, 1, 23040),), out / "mimi_encoder.onnx",
            ["audio"], ["conditioning"], {"audio": {2: "samples"}, "conditioning": {1: "frames"}})

    layers = len(model.flow_lm.transformer.layers)
    attn = model.flow_lm.transformer.layers[0].self_attn
    states, names_in, names_out = [], [], []
    for i in range(layers):
        states += [torch.zeros(1, MAX_SEQ, attn.num_heads, attn.dim_per_head, dtype=torch.float16),
                   torch.zeros(1, MAX_SEQ, attn.num_heads, attn.dim_per_head, dtype=torch.float16),
                   torch.tensor([10], dtype=torch.long)]
    for i in range(3 * layers):
        names_in.append(f"state_{i}")
        names_out.append(f"out_state_{i}")
    _export(FlowLmMain(model), (torch.randn(1, 1, 32), torch.zeros(1, 0, 1024), *states), out / "flow_lm_main.onnx",
            ["sequence", "text_embeddings", *names_in], ["conditioning", "eos_logit", *names_out],
            {"sequence": {1: "seq_len"}, "text_embeddings": {1: "text_len"}})

    _export(FlowHead(model), (torch.randn(1024), torch.zeros(1, 1), torch.ones(1, 1), torch.randn(1, 32)),
            out / "flow_lm_flow.onnx", ["c", "s", "t", "x"], ["flow_dir"], {})
    _fix_identity_initializers(out / "flow_lm_flow.onnx")

    _patch_streaming_for_onnx()
    decoder = MimiDecoder(model)
    layout, init = _state_layout(model.mimi)
    flat = [init[m][k] for m, k in layout]
    _export(decoder, (torch.randn(1, 1, 32), *flat), out / "mimi_decoder.onnx",
            ["latent", *[f"state_{i}" for i in range(len(flat))]],
            ["audio_frame", *[f"out_state_{i}" for i in range(len(flat))]],
            {"latent": {1: "latent_frames"}, "audio_frame": {2: "audio_samples"}})

    quantize(out, keep_fp32)


def quantize(out: Path, keep_fp32: bool = False):
    import onnx
    from onnxruntime.quantization import QuantType, quantize_dynamic

    main = onnx.load(str(out / "flow_lm_main.onnx"))
    mlp = [n.name for n in main.graph.node if n.op_type == "MatMul" and ("linear1" in n.name or "linear2" in n.name)]
    assert mlp, "no MLP MatMuls found in flow_lm_main"
    quantize_dynamic(str(out / "flow_lm_main.onnx"), str(out / "flow_lm_main_int8.onnx"),
                     weight_type=QuantType.QInt8, op_types_to_quantize=["MatMul"], nodes_to_quantize=mlp)
    for name in ("flow_lm_flow", "mimi_decoder"):
        quantize_dynamic(str(out / f"{name}.onnx"), str(out / f"{name}_int8.onnx"),
                         weight_type=QuantType.QInt8, op_types_to_quantize=["MatMul"])
    if not keep_fp32:
        for name in ("flow_lm_main", "flow_lm_flow", "mimi_decoder"):
            (out / f"{name}.onnx").unlink()


def save_bos(model: TTSModel, out: Path):
    if model.flow_lm.insert_bos_before_voice:
        np.save(out / "bos_before_voice.npy", model.flow_lm.bos_before_voice.detach().float().numpy().reshape(1, 1, -1))
