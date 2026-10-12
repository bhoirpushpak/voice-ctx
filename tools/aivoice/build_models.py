"""Builds the complete Voice CTX AI Voice model set from pinned, checksummed upstream sources.

Output layout (default: %APPDATA%/VoiceCtx/AiVoice):
    content_encoder.onnx   HuBERT-base content features (MIT, via RVC)
    rmvpe.onnx             RMVPE pitch estimator (MIT, via RVC)
    voices/<id>.onnx|json  streaming voice generators
    CREDITS.txt

Usage:  python build_models.py [--out DIR] [--cache DIR]
"""
from __future__ import annotations

import argparse
import os
import shutil
from pathlib import Path

import rvc_export as rx

RVC_HF = "https://huggingface.co/lj1995/VoiceConversionWebUI/resolve/e6d0c1a17da07c33557852f9dfa2bd44cc75737d"
VCTK_HF = "https://huggingface.co/Nekochu/RVC-VCTK_Voice-sample/resolve/005c2f948ee9dafd7e3aa7f261b4c3a24beebeef"

BASE_FILES = {
    "hubert_base.pt": ("F54B40FD2802423A5643779C4861AF1E9EE9C1564DC9D32F54F20B5FFBA7DB96", f"{RVC_HF}/hubert_base.pt"),
    "rmvpe.onnx": ("5370E71AC80AF8B4B7C793D27EFD51FD8BF962DE3A7EDE0766DAC0BEFA3660FD", f"{RVC_HF}/rmvpe.onnx"),
}

VCTK_CREDIT = ("Trained on the CSTR VCTK Corpus (Centre for Speech Technology Research, University of Edinburgh), "
               "CC BY 4.0. RVC model by Nekochu, Apache-2.0.")

STOCK_VOICES = [
    {
        "id": "vctk-p226-male",
        "name": "Narrator (male)",
        "file": "M/p226/rmvpe/Mp226rmvpe.pth",
        "sha256": "0A1D55E17CF08C922596710E58A0862CB54C8EA2AB037A5FC1F85C5F08AB4B18",
        "gender": "male",
        "defaultPitchShift": 0,
    },
    {
        "id": "vctk-p231-female",
        "name": "Presenter (female)",
        "file": "F/p231/rmvpe/Fp231rmvpe.pth",
        "sha256": "12F819E129C6E48779439E491B85593B8819D094F751C3515EA13BE6BD8B5A59",
        "gender": "female",
        "defaultPitchShift": 12,
    },
]

CREDITS = f"""Voice CTX — AI Voice model credits

Content encoder: HuBERT base (Meta AI, MIT) as distributed by the RVC project (MIT),
  https://huggingface.co/lj1995/VoiceConversionWebUI
Pitch estimator: RMVPE (MIT) as distributed by the RVC project,
  https://huggingface.co/lj1995/VoiceConversionWebUI
Voice architecture: Retrieval-based Voice Conversion (RVC) v2, MIT,
  https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI
Stock voices: {VCTK_CREDIT}
  https://huggingface.co/Nekochu/RVC-VCTK_Voice-sample
  https://datashare.ed.ac.uk/handle/10283/3443
"""


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", type=Path, default=Path(os.environ["APPDATA"]) / "VoiceCtx" / "AiVoice")
    ap.add_argument("--cache", type=Path, default=Path.home() / ".cache" / "voicectx-aivoice")
    a = ap.parse_args()

    rx.ensure_rvc_source(a.cache)
    for name, (sha, url) in BASE_FILES.items():
        rx.download(url, a.cache / name, sha)

    a.out.mkdir(parents=True, exist_ok=True)
    print("exporting content encoder")
    rx.export_content_encoder(a.cache / "hubert_base.pt", a.out / "content_encoder.onnx")
    shutil.copyfile(a.cache / "rmvpe.onnx", a.out / "rmvpe.onnx")

    for v in STOCK_VOICES:
        pth = rx.download(f"{VCTK_HF}/{v['file']}", a.cache / "voices" / Path(v["file"]).name, v["sha256"])
        print(f"exporting voice {v['id']}")
        rx.export_voice(pth, a.out / "voices", v["id"], {
            "name": v["name"], "gender": v["gender"], "defaultPitchShift": v["defaultPitchShift"],
            "credits": VCTK_CREDIT, "license": "CC BY 4.0 (data) / Apache-2.0 (model)", "builtIn": True,
        })

    (a.out / "CREDITS.txt").write_text(CREDITS, encoding="utf-8")
    print(f"done: {a.out}")


if __name__ == "__main__":
    main()
