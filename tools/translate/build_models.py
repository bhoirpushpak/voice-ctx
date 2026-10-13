"""Builds the Voice CTX Live translate model pack from pinned, checksummed upstream sources.

Output layout (default: %APPDATA%/VoiceCtx/Translate):
    whisper/ggml-large-v3-turbo-q5_0.bin   speech-to-text (whisper.cpp, MIT)
    vad/silero_vad.onnx                    speech detection (Silero VAD v6.2.1, MIT)
    nllb/encoder_model.onnx                translation encoder (NLLB-200 distilled 600M, CC BY-NC 4.0, int8)
    nllb/decoder_model_merged.onnx         translation decoder with KV cache (int8)
    nllb/sentencepiece.bpe.model           tokenizer model
    nllb/vocab.json                        token -> id map (includes language tokens)
    CREDITS.txt

Usage:  python build_models.py [--out DIR] [--cache DIR]
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import urllib.request
from pathlib import Path

WHISPER_URL = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/"
               "ggml-large-v3-turbo-q5_0.bin")
WHISPER_SHA = "394221709CD5AD1F40C46E6031CA61BCE88931E6E088C188294C6D5A55FFA7E2"
SILERO_URL = "https://raw.githubusercontent.com/snakers4/silero-vad/v6.2.1/src/silero_vad/data/silero_vad.onnx"
SILERO_SHA = "1A153A22F4509E292A94E67D6F9B85E8DEB25B4988682B7E174C65279D8788E3"
# NLLB-200 distilled 600M (Meta, CC BY-NC 4.0), ONNX conversion with 8-bit weights by Xenova
# (as used by Transformers.js), pinned to an exact revision and verified by SHA-256.
NLLB_ONNX_BASE = "https://huggingface.co/Xenova/nllb-200-distilled-600M/resolve/261c31d1a5732c67cdd16d80e8d6088507c7ccea"
NLLB_ONNX = {
    "encoder_model_quantized.onnx": ("5CDE664EACBA07A62F198857EC6C06E09572B1EBB77C8137F1FA99AC604A3A28", "encoder_model.onnx"),
    "decoder_model_merged_quantized.onnx": ("DD66608C2A4194E78F95548FA0E64F24302303698C5B09FA8E1F9E16EC00676B", "decoder_model_merged.onnx"),
}
NLLB_TOKENIZER = {
    "sentencepiece.bpe.model": "14BB8DFB35C0FFDEA7BC01E56CEA38B9E3D5EFCDCB9C251D6B40538E1AAB555A",
    "tokenizer.json": "8AC789AD7DABEA44D41537822D48C516BA358374C51813E2CBA78C006E150C94",
    "tokenizer_config.json": None,
    "special_tokens_map.json": None,
    "config.json": None,
}


def _fetch(url: str, dest: Path) -> Path:
    """Small JSON metadata files (no checksum published); content is validated by the tokenizer load."""
    dest.parent.mkdir(parents=True, exist_ok=True)
    if not dest.exists():
        urllib.request.urlretrieve(url, dest)
    return dest

CREDITS = """Voice CTX - Live translate model credits

Speech-to-text: OpenAI Whisper large-v3-turbo (MIT), ggml q5_0 conversion by the whisper.cpp project (MIT),
  https://huggingface.co/ggerganov/whisper.cpp
Speech detection: Silero VAD v6.2.1 (MIT), https://github.com/snakers4/silero-vad
Translation: NLLB-200 distilled 600M by Meta AI, CC BY-NC 4.0 (non-commercial use only),
  https://huggingface.co/facebook/nllb-200-distilled-600M
  ONNX conversion with 8-bit weights by Xenova, https://huggingface.co/Xenova/nllb-200-distilled-600M
"""


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def download(url: str, dest: Path, expected: str) -> Path:
    if dest.exists() and sha256(dest) == expected:
        return dest
    dest.parent.mkdir(parents=True, exist_ok=True)
    tmp = dest.with_suffix(dest.suffix + ".part")
    print(f"downloading {url}")
    urllib.request.urlretrieve(url, tmp)
    actual = sha256(tmp)
    if actual != expected:
        tmp.unlink()
        raise RuntimeError(f"SHA-256 mismatch for {url}: expected {expected}, got {actual}")
    tmp.replace(dest)
    return dest


def fuse_lm_head(path: Path) -> int:
    """Speeds up the NLLB decoder ~4x on CPU.

    The 8-bit export stores the 256k-word output projection as 8-bit, then on *every* decoding step
    transposes it and converts it back to float (Transpose -> DequantizeLinear) before a float
    MatMul. Store the weights pre-transposed and use ONNX Runtime's DynamicQuantizeMatMul, which
    multiplies against the 8-bit weights directly (the kernel every other layer already uses).
    Returns the number of rewrites.
    """
    import numpy as np
    import onnx
    from onnx import helper, numpy_helper

    model = onnx.load(str(path))
    outer = {i.name: i for i in model.graph.initializer}
    transposed: dict[str, str] = {}
    rewrites = 0

    def pretransposed(name: str) -> str:
        """Adds (once) a pre-transposed copy of a 2-D initializer to the main graph."""
        if name not in transposed:
            array = numpy_helper.to_array(outer[name])
            new_name = name + "_pretransposed"
            model.graph.initializer.append(numpy_helper.from_array(np.ascontiguousarray(array.T), new_name))
            outer[new_name] = model.graph.initializer[-1]
            transposed[name] = new_name
        return transposed[name]

    def visit(graph, scope):
        nonlocal rewrites
        inits = {**scope, **{i.name: i for i in graph.initializer}}
        for node in list(graph.node):
            for attr in node.attribute:
                if attr.type == onnx.AttributeProto.GRAPH:
                    visit(attr.g, inits)
        consumers = {}
        for node in graph.node:
            for name in node.input:
                consumers.setdefault(name, []).append(node)
        producers = {o: n for n in graph.node for o in n.output}
        for dq in [n for n in graph.node if n.op_type == "DequantizeLinear"]:
            users = consumers.get(dq.output[0], [])
            if len(users) != 1 or users[0].op_type != "MatMul" or users[0].input[1] != dq.output[0]:
                continue
            weight_name = dq.input[0]
            transpose = producers.get(weight_name)
            if transpose is not None:
                perm = next((list(a.ints) for a in transpose.attribute if a.name == "perm"), [1, 0])
                if transpose.op_type != "Transpose" or perm != [1, 0] or transpose.input[0] not in outer:
                    continue
                weight_name = pretransposed(transpose.input[0])
            weight = outer.get(weight_name) or inits.get(weight_name)
            scale = inits.get(dq.input[1])
            zero = inits.get(dq.input[2]) if len(dq.input) > 2 else None
            if weight is None or scale is None or len(weight.dims) != 2:
                continue
            n_cols = weight.dims[1]
            axis = next((a.i for a in dq.attribute if a.name == "axis"), 1)
            per_tensor = len(scale.dims) == 0 or (len(scale.dims) == 1 and scale.dims[0] == 1)
            per_column = len(scale.dims) == 1 and scale.dims[0] == n_cols and axis in (1, -1)
            print(f"  lm_head candidate {weight.name} {list(weight.dims)} scale {list(scale.dims)} axis {axis}")
            if not (per_tensor or per_column):
                continue
            matmul = users[0]
            fused = helper.make_node(
                "DynamicQuantizeMatMul",
                [matmul.input[0], weight_name, dq.input[1]] + ([dq.input[2]] if zero is not None else []),
                list(matmul.output), name=matmul.name + "_int8", domain="com.microsoft")
            index = list(graph.node).index(matmul)
            graph.node.remove(matmul)
            graph.node.insert(index, fused)
            graph.node.remove(dq)
            if transpose is not None and len(consumers.get(transpose.output[0], [])) == 1:
                graph.node.remove(transpose)
            rewrites += 1

    visit(model.graph, outer)
    if rewrites:
        if not any(o.domain == "com.microsoft" for o in model.opset_import):
            model.opset_import.append(helper.make_opsetid("com.microsoft", 1))
        onnx.save(model, str(path))
    return rewrites


def export_nllb(out: Path, cache: Path) -> None:
    from transformers import AutoTokenizer

    out.mkdir(parents=True, exist_ok=True)
    for name, (sha, target) in NLLB_ONNX.items():
        model = download(f"{NLLB_ONNX_BASE}/onnx/{name}", cache / "nllb" / name, sha)
        shutil.copyfile(model, out / target)
    print(f"optimized decoder output layer: {fuse_lm_head(out / 'decoder_model_merged.onnx')} rewrite(s)")
    tok_dir = cache / "nllb-tokenizer"
    for name, sha in NLLB_TOKENIZER.items():
        url = f"{NLLB_ONNX_BASE}/{name}"
        if sha:
            download(url, tok_dir / name, sha)
        else:
            _fetch(url, tok_dir / name)
    shutil.copyfile(tok_dir / "sentencepiece.bpe.model", out / "sentencepiece.bpe.model")
    tok = AutoTokenizer.from_pretrained(str(tok_dir))
    vocab = tok.get_vocab()
    meta = {
        "vocab": vocab,
        "bos": tok.bos_token_id, "eos": tok.eos_token_id, "pad": tok.pad_token_id, "unk": tok.unk_token_id,
        "decoderStart": 2,
        "languages": sorted(k for k in vocab if len(k) == 8 and k[3] == "_"),
    }
    (out / "vocab.json").write_text(json.dumps(meta, ensure_ascii=False), encoding="utf-8")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", type=Path, default=Path(os.environ["APPDATA"]) / "VoiceCtx" / "Translate")
    ap.add_argument("--cache", type=Path, default=Path.home() / ".cache" / "voicectx-translate")
    a = ap.parse_args()

    whisper = download(WHISPER_URL, a.cache / "ggml-large-v3-turbo-q5_0.bin", WHISPER_SHA)
    silero = download(SILERO_URL, a.cache / "silero_vad.onnx", SILERO_SHA)
    (a.out / "whisper").mkdir(parents=True, exist_ok=True)
    (a.out / "vad").mkdir(parents=True, exist_ok=True)
    shutil.copyfile(whisper, a.out / "whisper" / whisper.name)
    shutil.copyfile(silero, a.out / "vad" / silero.name)
    export_nllb(a.out / "nllb", a.cache)
    (a.out / "CREDITS.txt").write_text(CREDITS, encoding="utf-8")
    print(f"done: {a.out}")


if __name__ == "__main__":
    main()
