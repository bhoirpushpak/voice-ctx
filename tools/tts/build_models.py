"""Builds the Voice CTX speech models (%AppData%\\VoiceCtx\\Speech\\<language>).

Kyutai Pocket TTS (English, French, German, Spanish, Italian, Portuguese, Dutch) is exported to
ONNX from the weights pinned by pocket-tts 3.3.0. Those weights are gated: sign in with
`hf auth login` and accept the terms at https://huggingface.co/kyutai/pocket-tts first.

Hindi uses the ONNX release of Pocket TTS Hindi by Saryps Labs, pinned and checksum-verified.

Usage:
    python build_models.py                     # all eight languages
    python build_models.py --languages en hi   # some of them
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys
from pathlib import Path

os.environ.setdefault("HF_HUB_DISABLE_XET", "1")

from huggingface_hub import hf_hub_download  # noqa: E402

KYUTAI = {
    # code: (pocket-tts language config, display name)
    "en": ("english", "English"),
    "fr": ("french", "French"),
    "de": ("german", "German"),
    "es": ("spanish", "Spanish"),
    "it": ("italian", "Italian"),
    "pt": ("portuguese", "Portuguese"),
    "nl": ("dutch", "Dutch"),
}

HINDI_REPO = "saryps-labs/pocket-tts-hindi"
HINDI_REVISION = "6bff9c1e776bb90c68dd05c2467c62d1ecdf1b51"
HINDI_FILES = {
    "text_conditioner.onnx": "3f3d7744da63d975ecaf04f131eec28aea1f91dacd9b06a975c863e3391523e2",
    "mimi_encoder.onnx": "f88ed9c97c61b7adf26c1f82a9468196dab5d67387e1332f3f08c720d86f11cd",
    "flow_lm_main_int8.onnx": "f7592dfc5c74bea1b8d6c8e2d6839806d44d833c4995f6da5bb5bddc20857002",
    "flow_lm_flow_int8.onnx": "a48edf56bafc9874e053700a990395691def264bad0eb6cf152fd520a082545d",
    "mimi_decoder_int8.onnx": "62f6540215bba4bd1ef6a55423999e2a18fab0d179ead5573edaedf5c8ab5e33",
    "bos_before_voice.npy": "c8989f34dfe072c9faf96ce9f836873c7d09b27526187b6c58583260bbbb6aa4",
    "tokenizer.model": "27e09a7096f9d4912e0642182d421458baeeddbf8f25018e77417bf260535326",
}

KYUTAI_CREDITS = ("Kyutai Pocket TTS ({name}), CC BY 4.0, https://huggingface.co/kyutai/pocket-tts. "
                  "Exported to ONNX and quantized to 8-bit by Voice CTX.")
HINDI_CREDITS = ("Pocket TTS Hindi by Saryps Labs, based on Kyutai Pocket TTS and trained with AI4Bharat "
                 "IndicVoices and Kathbath data, CC BY 4.0, https://huggingface.co/saryps-labs/pocket-tts-hindi. "
                 "ONNX conversion by Jim Grimsby Jr. (Sonata Sherpa Voices).")

VOICES_REPO = "kyutai/tts-voices"
VOICES_REVISION = "323332d33f997de8394f24a193e1a76df720e01a"
# Ready-made voices to use until you record your own (VCTK corpus, CC BY 4.0, cleaned up by Kyutai).
PRESET_VOICES = {
    "narrator-male": ("Narrator (male)", "vctk/p226_023_enhanced.wav"),
    "presenter-female": ("Presenter (female)", "vctk/p231_023_enhanced.wav"),
}
VCTK_CREDITS = ("CSTR VCTK Corpus, Centre for Speech Technology Research, University of Edinburgh, CC BY 4.0, "
                "https://datashare.ed.ac.uk/handle/10283/3443; clip cleaned up by Kyutai, https://huggingface.co/kyutai/tts-voices")

TOKENIZER_CHECK = [
    "Hello world. I'm Kyutai's Pocket TTS, fast enough for small CPUs!",
    "Bonjour, je reviens dans cinq minutes. Ça va ?",
    "Ich bin gleich zurück – in fünf Minuten.",
    "¿Dónde está la estación? Está a 300 metros.",
]


def default_root() -> Path:
    return Path(os.environ["APPDATA"]) / "VoiceCtx" / "Speech"


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def write_settings(folder: Path, settings: dict):
    (folder / "voicectx.json").write_text(json.dumps(settings, indent=2, ensure_ascii=False), encoding="utf-8")


def build_kyutai(code: str, out: Path):
    import torch  # noqa: F401  (imported lazily: Hindi alone needs no PyTorch)
    from pocket_tts.models.tts_model import TTSModel
    from pocket_tts.modules.text_conditioner import JsonTokenizer, SentencePieceTokenizer
    from pocket_tts.utils.config import CONFIGS_DIR, load_config

    import export_pocket_tts as exporter

    language, name = KYUTAI[code]
    print(f"\n== {name} ({language})")
    config = load_config(CONFIGS_DIR / f"{language}.yaml")
    model = TTSModel.load_model(language=language)
    if not model.has_voice_cloning:
        sys.exit("Voice cloning weights are not available. Run `hf auth login` and accept the terms at "
                 "https://huggingface.co/kyutai/pocket-tts, then try again.")

    tmp = out.with_name(out.name + ".partial")
    shutil.rmtree(tmp, ignore_errors=True)
    exporter.export_all(model, tmp)
    exporter.save_bos(model, tmp)

    # The configs point at tokenizer.json; Voice CTX reads the SentencePiece model it was converted from.
    json_path = config.flow_lm.lookup_table.tokenizer_path
    repo_path, revision = json_path.removeprefix("hf://").split("@")
    repo = "/".join(repo_path.split("/")[:2])
    spm_name = "/".join(repo_path.split("/")[2:]).replace("tokenizer.json", "tokenizer.model")
    spm = Path(hf_hub_download(repo, spm_name, revision=revision))
    n_bins = config.flow_lm.lookup_table.n_bins
    reference, candidate = JsonTokenizer(n_bins, json_path), SentencePieceTokenizer(n_bins, str(spm))
    for text in TOKENIZER_CHECK:
        assert reference.encode(text) == candidate.encode(text), f"tokenizer mismatch on {text!r}"
    shutil.copy(spm, tmp / "tokenizer.model")

    write_settings(tmp, {
        "language": code,
        "name": name,
        "temperature": config.default_temperature,
        "lsdSteps": 1,
        "eosThreshold": -4.0,
        "framesAfterEos": config.model_recommended_frames_after_eos,
        "appendTerminalPunctuation": config.append_terminal_punctuation,
        "capitalizeFirstLetter": config.capitalize_first_letter,
        "removeSemicolons": config.remove_semicolons,
        "padShortInputs": config.pad_with_spaces_for_short_inputs,
        "stripPunctuation": False,
        "replaceCharacters": config.replace_characters,
        "credits": KYUTAI_CREDITS.format(name=name),
        "license": "CC BY 4.0",
    })
    shutil.rmtree(out, ignore_errors=True)
    tmp.rename(out)


def build_hindi(out: Path):
    print("\n== Hindi")
    tmp = out.with_name(out.name + ".partial")
    shutil.rmtree(tmp, ignore_errors=True)
    tmp.mkdir(parents=True)
    for name, expected in HINDI_FILES.items():
        path = Path(hf_hub_download(HINDI_REPO, f"onnx/{name}", revision=HINDI_REVISION))
        actual = sha256(path)
        if actual != expected:
            sys.exit(f"Checksum mismatch for {name}: {actual}")
        shutil.copy(path, tmp / name)
        print(f"  {name} ok")
    write_settings(tmp, {
        "language": "hi",
        "name": "Hindi",
        "temperature": 0.3,
        "lsdSteps": 1,
        "eosThreshold": -1.0,
        "appendTerminalPunctuation": False,
        "capitalizeFirstLetter": False,
        "stripPunctuation": True,   # trained without punctuation
        "credits": HINDI_CREDITS,
        "license": "CC BY 4.0",
    })
    shutil.rmtree(out, ignore_errors=True)
    tmp.rename(out)


def fetch_voices(root: Path):
    folder = root / "voices"
    folder.mkdir(parents=True, exist_ok=True)
    for voice_id, (name, path) in PRESET_VOICES.items():
        source = Path(hf_hub_download(VOICES_REPO, path, revision=VOICES_REVISION))
        shutil.copy(source, folder / f"{voice_id}.wav")
        (folder / f"{voice_id}.json").write_text(json.dumps(
            {"name": name, "builtIn": True, "credits": VCTK_CREDITS, "license": "CC BY 4.0"}, indent=2), encoding="utf-8")
        print(f"  voice {name} ok")


def write_credits(root: Path):
    lines = ["Voice CTX - speech model credits", ""]
    for folder in sorted(p for p in root.iterdir() if (p / "voicectx.json").exists()):
        settings = json.loads((folder / "voicectx.json").read_text(encoding="utf-8"))
        lines.append(f"{settings['name']}: {settings['credits']}")
    lines += ["", f"Ready-made voices (Narrator, Presenter): {VCTK_CREDITS}"]
    lines += ["", "Runtime design follows PocketTTS.cpp (MIT), https://github.com/VolgaGerm/PocketTTS.cpp.",
              "Only clone voices with the speaker's consent. Don't use synthetic speech to impersonate or deceive."]
    (root / "CREDITS.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--languages", nargs="+", default=[*KYUTAI, "hi"], choices=[*KYUTAI, "hi"])
    parser.add_argument("--output", type=Path, default=default_root())
    args = parser.parse_args()
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    args.output.mkdir(parents=True, exist_ok=True)
    for code in args.languages:
        if code == "hi":
            build_hindi(args.output / "hi")
        else:
            build_kyutai(code, args.output / code)
    fetch_voices(args.output)
    write_credits(args.output)
    print(f"\nDone: {args.output}")


if __name__ == "__main__":
    main()
