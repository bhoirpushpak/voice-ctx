# Voice CTX - Voice Change Transform Xpress

**A free, real-time voice changer for Windows.** Voice CTX takes your microphone, cleans it up,
changes your voice and sends the result to a virtual microphone. Discord, games, OBS, Zoom or
any other app can then use that virtual microphone and hear your changed voice.

<p align="center">
  <a href="https://github.com/bhoirpushpak/voice-ctx/releases/latest/download/VoiceCtx.exe">
    <img src="https://img.shields.io/badge/Download-VoiceCtx.exe-7C5CFF?style=for-the-badge&logo=windows&logoColor=white" alt="Download VoiceCtx.exe" height="44">
  </a>
</p>

<p align="center">
  <a href="https://github.com/bhoirpushpak/voice-ctx/releases/latest"><img src="https://img.shields.io/github/v/release/bhoirpushpak/voice-ctx?label=latest&style=flat-square" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011%20(64--bit)-0078D6?style=flat-square" alt="Windows 10 | 11 64-bit">
  <a href="https://github.com/bhoirpushpak/voice-ctx/releases"><img src="https://img.shields.io/github/downloads/bhoirpushpak/voice-ctx/total?style=flat-square" alt="Downloads"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-green?style=flat-square" alt="MIT license"></a>
</p>

<p align="center">
  <b>Also needed:</b> <a href="https://vb-audio.com/Cable/">VB-Audio Virtual Cable</a> (free) &nbsp;·&nbsp;
  <b>Optional:</b> <a href="https://github.com/bhoirpushpak/voice-ctx/releases/latest/download/VoiceCtx-AiVoice-Models-v1.zip">AI Voice model pack</a> (~700 MB)<br>
  <sub>A single file with no installer. If Windows SmartScreen warns you, click <b>More info → Run anyway</b>.</sub>
</p>

- **15 voice effects:** Male, Female, Baby, Helium, Custom pitch, Robot, Alien, Atari, Clone,
  Mutation (normal, fast and slow), Radio, Silence and None.
- **AI Voice:** real-time AI voice conversion, so you sound like a different person. Your words,
  timing and language stay your own. It ships with two built-in voices and lets you import your own.
- **Mic enhancement:**
  - **Noise cancellation:** AI noise removal for fans, keyboards, traffic and background chatter.
  - **Studio sound:** a broadcast-style vocal chain (EQ, de-esser, compressor and limiter).
- **Built for live use:**
  - Low latency.
  - Click-free switching between effects.
  - Global hotkeys that work in-game.
  - Runs in the system tray.
  - Recovers by itself if you unplug a headset.

<p align="center"><i>Your mic → Voice CTX → "CABLE Output" → Discord / games / OBS</i></p>

---

## Contents

1. [System requirements](#system-requirements)
2. [Prerequisites](#prerequisites)
3. [Quick start](#quick-start)
4. [Installing VB-Audio Virtual Cable](#installing-vb-audio-virtual-cable)
5. [Launching Voice CTX](#launching-voice-ctx)
6. [Using it in Discord, OBS and games](#using-it-in-discord-obs-and-games)
7. [Feature guide](#feature-guide)
8. [AI Voice setup](#ai-voice-setup)
9. [Hotkeys](#hotkeys)
10. [Where your data is stored](#where-your-data-is-stored)
11. [Troubleshooting](#troubleshooting)
12. [Building from source](#building-from-source)
13. [Project layout](#project-layout)
14. [Credits and licenses](#credits-and-licenses)

---

## System requirements

| | Minimum | Recommended |
|---|---|---|
| **OS** | Windows 10 64-bit (version 1903 or later) | Windows 11 64-bit |
| **CPU** | Any 64-bit dual-core | 4+ cores |
| **RAM** | 4 GB | 8 GB, or 16 GB if you use AI Voice |
| **Disk** | 100 MB | 1.2 GB with the AI Voice model pack |
| **Audio** | Any microphone or headset | A headset, so your speakers don't feed back into the mic |
| **GPU** | Not needed for effects, noise cancellation or studio sound | **Required in practice for AI Voice:** a DirectX 12 GPU with 2 GB+ of video memory. A discrete NVIDIA, AMD or Intel Arc GPU is recommended. |

**How heavy each feature is:**

| Feature | Load | Added delay |
|---|---|---|
| Voice effects and studio sound | Under 1% of one CPU core | — |
| Noise cancellation | About 1.5% of a core | About 20 ms |
| AI Voice | About 28 ms of GPU time per 100 ms of audio on an RTX 4060 Laptop GPU | About 180 ms |

Without a GPU, AI Voice falls back to the CPU, which is usually too slow. When that happens the
app keeps your normal voice and tells you.

## Prerequisites

| What | Needed for | Where to get it |
|---|---|---|
| **VB-Audio Virtual Cable** (free) | **Required.** It's the virtual microphone other apps listen to. | [vb-audio.com/Cable](https://vb-audio.com/Cable/) |
| **AI Voice model pack** (~900 MB) | Optional, only for the AI Voice tab | The [Releases](../../releases) page (`VoiceCtx-AiVoice-Models-*.zip`) |
| .NET 8 SDK | Only for building from source. The release `.exe` already contains everything. | [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0) |

You **don't** need to install .NET, Visual C++ runtimes or anything else to run the release exe.

## Quick start

1. **Install VB-Audio Virtual Cable** ([step by step](#installing-vb-audio-virtual-cable)) and reboot.
2. **[Download `VoiceCtx.exe`](https://github.com/bhoirpushpak/voice-ctx/releases/latest/download/VoiceCtx.exe)**
   and put it anywhere, for example `C:\Tools\VoiceCtx\`.
3. **Double-click `VoiceCtx.exe`.** Windows SmartScreen may warn you because the exe isn't
   code-signed; click **More info → Run anyway**.
4. In Voice CTX, choose your **Microphone**. **Output** should already say *CABLE Input*.
5. Pick an effect, for example **Robot**, and make sure the big switch says **ON**.
6. In Discord (or any app), set the **input device / microphone** to **CABLE Output**.

That's it. Your friends now hear the changed voice.

## Installing VB-Audio Virtual Cable

1. Go to <https://vb-audio.com/Cable/> and download the **VBCABLE Driver Pack** zip.
2. Extract the zip, right-click **`VBCABLE_Setup_x64.exe`** and choose **Run as administrator**.
3. Click **Install Driver** and wait for the confirmation.
4. **Reboot** your PC.
5. Check: open **Settings → System → Sound**. You should see **CABLE Input** under *Output* and
   **CABLE Output** under *Input*.

> **Make sure CABLE Input isn't muted.** Windows sometimes mutes new devices. Open
> **Settings → System → Sound → All sound devices → CABLE Input** and make sure *Allow* is on and
> the volume isn't muted. Voice CTX's status bar warns you if the output device is muted.

If you skip the cable, Voice CTX still runs, but it shows a reminder with the download link,
because there's nowhere to send your voice.

## Launching Voice CTX

### From the release (recommended)

- Run **`VoiceCtx.exe`**. It's a single self-contained file, with no installer.
- The first launch takes a few seconds while Windows unpacks the exe. Later launches are quick.
- **Start with Windows (optional):** press `Win + R`, type `shell:startup`, press Enter, and put
  a shortcut to `VoiceCtx.exe` in that folder.
- Only one copy runs at a time. Starting it again just brings the existing window to the front.

### From source

```powershell
git clone https://github.com/bhoirpushpak/voice-ctx.git
cd voice-ctx
dotnet run --project src/VoiceCtx.App -c Release
```

### Closing and the tray icon

- **Closing the window keeps Voice CTX running** in the system tray, so your voice keeps flowing to
  Discord. Click the tray icon to bring the window back.
- **To really quit**, right-click the tray icon and choose **Exit**.
- The tray icon is **purple** while the effect is on and **grey** while it's off.

## Using it in Discord, OBS and games

**Discord**
1. Open **User Settings → Voice & Video**.
2. Set **Input Device** to **CABLE Output (VB-Audio Virtual Cable)**.
3. Click **Let's Check** and talk. You should hear the effect.
4. Recommended: turn **off** Discord's *Noise Suppression* and *Echo Cancellation*, and use Voice
   CTX's own **Noise cancellation** instead. Discord's filters are tuned for natural voices and can
   garble effects.

**OBS Studio:** add an **Audio Input Capture** source and choose **CABLE Output**.

**Games, Zoom, Teams and others:** in the app's audio or voice settings, choose **CABLE Output**
as the microphone.

**Hear yourself:** tick **Hear myself** in Voice CTX and choose your **headphones** as the
*Monitor* device. Use headphones, not speakers, or your mic will pick the sound back up.

## Feature guide

### The main switch (top-right)
**ON** applies your chosen effect. **OFF** sends your clean voice through, so apps never lose your
mic. The global hotkey is **Ctrl+Alt+V**.

### Devices
- **Microphone:** your real mic. *System default* follows Windows.
- **Output:** where the changed voice goes. Normally **CABLE Input**.
- **Monitor + Hear myself:** plays your changed voice in your headphones.

### Levels
- **Input level:** shows your mic is working.
- **Master volume:** 0–200% output volume.

### Mic enhancement
These are applied first, before any effect, and they also work with the **None** effect as a plain
"better mic":
- **Noise cancellation:** removes steady and sudden background noise. The slider sets how strong it
  is. Hotkey **Ctrl+Alt+N**.
- **Studio sound:** a rumble filter, gentle noise gate, warmth/presence EQ, de-esser, compressor
  and limiter. Your voice sounds closer, fuller and more even. The slider sets the strength.

### Voice effects (tab)

| Effect | What it does |
|---|---|
| None | Clean voice |
| Male / Female / Baby / Helium | Pitch shift by −3 / +4 / +8 / +12 semitones (natural-speed, real-time pitch shifting) |
| Custom pitch | Use the **Custom pitch** slider (−12 to +12 semitones) |
| Robot | Metallic comb filter plus ring modulation |
| Alien | Warbling high voice with a shimmering ring modulator |
| Atari | 8-bit crunch (low sample rate, 4-bit) |
| Clone | Your voice plus a slightly delayed, octave-up twin |
| Mutation / Fast / Slow | Pitch that keeps drifting up and down |
| Radio | Old AM-radio / telephone band with light distortion |
| Silence | Mutes your voice |

**Effect strength (dry/wet)** blends between your normal voice (0%) and the full effect (100%).
Switching effects crossfades over 20 ms, so it never clicks.

### AI Voice (tab)
See [AI Voice setup](#ai-voice-setup) below.

## AI Voice setup

AI Voice uses RVC (Retrieval-based Voice Conversion) models to turn your voice into another voice
in real time. It runs on your GPU through DirectML, so any DirectX 12 GPU works. On laptops with
two GPUs, Voice CTX automatically picks the more powerful one.

### 1. Install the model pack (one time)

1. Download **`VoiceCtx-AiVoice-Models-v1.zip`** (about 900 MB) from the [Releases](../../releases) page.
2. Press `Win + R`, type `%AppData%\VoiceCtx`, and press Enter. Create the folder first if it doesn't exist.
3. Extract the zip there, so that you end up with:

   ```
   %AppData%\VoiceCtx\AiVoice\content_encoder.onnx
   %AppData%\VoiceCtx\AiVoice\rmvpe.onnx
   %AppData%\VoiceCtx\AiVoice\voices\vctk-p226-male.onnx (+ .json)
   %AppData%\VoiceCtx\AiVoice\voices\vctk-p231-female.onnx (+ .json)
   %AppData%\VoiceCtx\AiVoice\CREDITS.txt
   ```

4. In Voice CTX, open the **AI Voice** tab and click **Refresh**, or restart the app.

Prefer to build the models yourself from the original sources? Use
[`tools/aivoice/build_models.py`](tools/aivoice) (see [Building from source](#building-from-source)).

### 2. Use it

1. Open the **AI Voice** tab and click a voice: **Narrator (male)** or **Presenter (female)**.
2. Flip the AI Voice switch to **ON**, or press **Ctrl+Alt+A**. The first time takes a few seconds
   while the models load onto your GPU. The status line then says *Running on <your GPU>*.
3. Adjust **Pitch shift** so the voice fits your range:
   - If you have a deeper voice and use a female voice, try **+12**.
   - The other way round, try **−12**.
   - Choosing a voice sets a sensible default.

Voice effects still apply on top. For example, AI Voice plus **Radio** gives you a radio presenter.

**Delay:** AI Voice adds about **180 ms**. That's fine for chatting and streaming, but you'll
notice it if you listen to yourself.

### 3. Add your own voices

Many RVC voices are shared online as `.pth` files. To use one:

1. Convert it once with the included Python tool (see [Building from source](#building-from-source)
   for the Python setup):

   ```powershell
   python tools/aivoice/convert_voice.py "C:\path\to\voice.pth" --name "My character" --gender male --pitch-shift 0
   ```

   This writes the converted voice straight into `%AppData%\VoiceCtx\AiVoice\voices\`.
2. In Voice CTX, click **Refresh**. The voice appears in the list.

Already-converted voices (an `.onnx` with its `.json`) can be added with **Import voice…**.
Only RVC **v2** voices with pitch guidance are supported. The converter reads `.pth` files in a
safe, weights-only mode, and Voice CTX itself never opens `.pth` files.

> **Please use AI voices responsibly.** Only use voices you have the rights to. Many voices
> online are clones of real people made without their consent. Never use AI Voice to impersonate
> someone or to deceive people.

## Hotkeys

These work everywhere, including in games:

| Hotkey | Action |
|---|---|
| **Ctrl+Alt+V** | Voice effects on/off |
| **Ctrl+Alt+N** | Noise cancellation on/off |
| **Ctrl+Alt+A** | AI Voice on/off |

If another app already uses one of these combinations, the status bar tells you which hotkey is
unavailable.

## Where your data is stored

| What | Location |
|---|---|
| Settings (devices, effect, sliders, toggles) | `%AppData%\VoiceCtx\settings.json` |
| AI Voice models and voices | `%AppData%\VoiceCtx\AiVoice\` |

Voice CTX works entirely offline. It doesn't collect, record or upload any audio or data. To
reset it, close the app from the tray and delete `settings.json`. To uninstall, delete
`VoiceCtx.exe` and the `%AppData%\VoiceCtx` folder.

## Troubleshooting

| Problem | Fix |
|---|---|
| **Friends hear nothing** | 1) In the app you're using, the microphone must be **CABLE Output**. 2) **CABLE Input** must not be muted in Windows Sound settings (the status bar warns you). 3) The Voice CTX status line should show *Microphone → CABLE Input*. |
| **"VB-Audio Virtual Cable not found"** | Install it ([guide](#installing-vb-audio-virtual-cable)), reboot, then restart Voice CTX. |
| **Input level doesn't move** | Choose the right **Microphone**. In **Settings → Privacy & security → Microphone**, allow desktop apps to use the microphone. |
| **Echo or feedback** | Use headphones for **Hear myself**, not speakers. |
| **Robotic, crackling or choppy sound** | Close heavy apps. In **Control Panel → Sound**, open your mic and the cable devices, and under *Properties → Advanced* set them all to **48000 Hz**. |
| **Noise cancellation is greyed out** | The bundled noise-removal component couldn't load. Re-download `VoiceCtx.exe`. |
| **AI Voice switch is greyed out** | The AI Voice model pack isn't installed. See [AI Voice setup](#ai-voice-setup). |
| **"Your PC can't keep up" in AI Voice** | The GPU is too slow or busy, so Voice CTX switches back to your normal voice for a few seconds. Close GPU-heavy apps, or check the status line names your dedicated GPU rather than the CPU. |
| **Hotkey does nothing** | Another app already uses that combination. The status bar says which one. |
| **SmartScreen warning when starting** | The exe isn't code-signed. Click **More info → Run anyway**, or build it yourself from source. |
| **Window disappeared** | It's in the system tray. Click the purple or grey icon near the clock, which may be under the **^** arrow. |

## Building from source

**Requirements:**
- Windows 10/11 x64.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- Git.

```powershell
git clone https://github.com/bhoirpushpak/voice-ctx.git
cd voice-ctx
dotnet build                                    # build everything
dotnet test                                     # run the test suite
dotnet run --project src/VoiceCtx.App           # run the app
```

**Make the single-file exe:**

```powershell
dotnet publish src/VoiceCtx.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The output is `src/VoiceCtx.App/bin/Release/net8.0-windows/win-x64/publish/VoiceCtx.exe`: one
compressed, self-contained file of about 75 MB.

**Rebuilding the noise-cancellation library (optional).** `rnnoise.dll` is already in the repo.
To rebuild it from the pinned upstream source, you need the Visual Studio C++ build tools. Run:

```powershell
powershell -ExecutionPolicy Bypass -File native/build-rnnoise.ps1
```

**Building the AI Voice models yourself (optional).** You need Python 3.10–3.12 and Git:

```powershell
cd tools/aivoice
python -m venv .venv
.venv\Scripts\activate
pip install -r requirements.txt
python build_models.py          # writes %AppData%\VoiceCtx\AiVoice
```

This downloads the pinned upstream models, verifies their SHA-256 checksums and exports them to
ONNX. `convert_voice.py` (see [Add your own voices](#3-add-your-own-voices)) uses the same Python
environment.

## Project layout

```
src/VoiceCtx.Audio/     audio engine and DSP (no UI)
  Engine/               WASAPI capture/render, lock-free FIFOs, drift-corrected resampling, device recovery
  Dsp/                  effect interface, pitch shifter, filters, dynamics
  Effects/              the voice effects + click-free effect switching
  Enhancement/          noise cancellation and studio sound
  AiVoice/              real-time RVC pipeline (ONNX Runtime + DirectML, GPU selection)
  Native/               rnnoise.dll (+ license)
src/VoiceCtx.App/       WPF app: UI, tray icon, hotkeys, settings
tests/VoiceCtx.Tests/   xUnit tests (effects, pitch accuracy, engine, AI Voice, settings)
tools/aivoice/          Python tools that build and convert AI Voice models
native/                 build script for rnnoise.dll
licenses/               third-party license texts
```

**How audio flows:**

```
mic ─► noise cancel ─► studio sound ─► AI Voice ─► voice effect ─► volume ─► CABLE Input ─► (CABLE Output in your apps)
                                                                      └─► monitor headphones (optional)
```

The audio thread never waits on locks or allocates memory. AI Voice inference runs on its own
worker thread, so a slow GPU can never stall your audio; if it can't keep up, you hear your
normal voice instead.

## Credits and licenses

Voice CTX is released under the [MIT License](LICENSE), © 2026 Pushpak Bhoir.

It builds on great open-source work:
- **NAudio:** MIT.
- **ONNX Runtime:** MIT.
- **DirectML:** Microsoft redistributable license.
- **RNNoise:** BSD-3-Clause.
- **RVC, HuBERT and RMVPE:** MIT.
- **Built-in AI voices:** trained on the **CSTR VCTK Corpus** (University of Edinburgh, CC BY 4.0),
  with RVC models by Nekochu (Apache-2.0).

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the [licenses/](licenses) folder for
details and full license texts.

VB-Audio Virtual Cable is a separate free product by VB-Audio Software and isn't included.
