"""Converts an RVC v2 voice (.pth) into a Voice CTX AI Voice model (.onnx + .json).

The checkpoint is loaded with torch.load(weights_only=True), so a malicious .pth cannot run code.
Only convert voices you have the rights to use.

Usage:
  python convert_voice.py my_voice.pth --name "My character" [--gender female] [--pitch-shift 12]
         [--out %APPDATA%/VoiceCtx/AiVoice/voices] [--cache DIR]
Then click "Refresh" in Voice CTX's AI Voice tab.
"""
from __future__ import annotations

import argparse
import os
import re
from pathlib import Path

import rvc_export as rx


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("pth", type=Path)
    ap.add_argument("--name", required=True)
    ap.add_argument("--gender", choices=["male", "female", "other"], default="other")
    ap.add_argument("--pitch-shift", type=int, default=0, help="suggested semitone shift for this voice")
    ap.add_argument("--credits", default="Imported by the user.")
    ap.add_argument("--out", type=Path, default=Path(os.environ["APPDATA"]) / "VoiceCtx" / "AiVoice" / "voices")
    ap.add_argument("--cache", type=Path, default=Path.home() / ".cache" / "voicectx-aivoice")
    a = ap.parse_args()

    rx.ensure_rvc_source(a.cache)
    voice_id = "user-" + (re.sub(r"[^a-z0-9]+", "-", a.name.lower()).strip("-") or "voice")
    path = rx.export_voice(a.pth, a.out, voice_id, {
        "name": a.name, "gender": a.gender, "defaultPitchShift": a.pitch_shift,
        "credits": a.credits, "license": "user supplied", "builtIn": False,
    })
    print(f"wrote {path}")


if __name__ == "__main__":
    main()
