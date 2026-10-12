# Voice CTX — Voice Change Transform Xpress

A real-time voice changer for Windows. Voice CTX captures your microphone, applies a voice
effect, and sends the result to the VB-Audio Virtual Cable. Discord, games, OBS or any other
app can then use **CABLE Output** as its microphone and hear your changed voice.

- 15 effects: None, Male, Female, Baby, Helium, Custom pitch (−12…+12 semitones), Robot,
  Alien, Atari, Clone, Mutation, Fast mutation, Slow mutation, Radio, Silence
- Real pitch shifting (time-domain WSOLA / granular overlap-add), not a playback-speed change
- **Mic enhancement**, applied before any voice effect, and also usable on its own with *None*:
  - **Noise cancellation**: AI noise removal with [RNNoise](https://github.com/xiph/rnnoise)
    for fans, keyboards, traffic and background chatter. It has an adjustable reduction amount and
    the hotkey **Ctrl+Alt+N**.
  - **Studio sound**: a broadcast vocal chain (rumble filter, noise gate, warmth/presence EQ,
    de-esser, compressor and limiter) with a strength slider.
- **AI Voice** (separate tab): real-time AI voice conversion with RVC. You sound like a different
  person, while your words, timing and language stay your own. It includes two built-in voices
  and lets you import more. The hotkey is **Ctrl+Alt+A**. See [AI Voice](#ai-voice) below.
- Dry/wet "effect strength" control, and click-free 20 ms crossfades when switching effects
- Big On/Off switch, plus the global hotkey **Ctrl+Alt+V**, which works while you're in a game
- Optional monitoring, so you can hear yourself in your headphones
- Tray icon: closing the window keeps your voice flowing; exit from the tray menu
- Recovers automatically when a device is unplugged or replugged
- Settings are saved to `%AppData%\VoiceCtx\settings.json`

## Requirements

- Windows 10 or 11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), to build from source. The
  published single-file exe needs nothing extra.
- [VB-Audio Virtual Cable](https://vb-audio.com/Cable/) (free). Voice CTX shows a reminder
  with this link if the cable isn't installed.

## Build and run

```powershell
dotnet build
dotnet test
dotnet run --project src/VoiceCtx.App
```

When it starts:

1. **Microphone:** pick your real mic, or leave it on *System default*.
2. **Output:** should already be **CABLE Input (VB-Audio Virtual Cable)**.
3. Pick an effect and make sure the switch in the top-right is **ON**.

Off means your clean voice still goes to the cable, so apps never lose your mic.

## Use it in Discord

1. Open Discord **User Settings → Voice & Video**.
2. Set **Input Device** to **CABLE Output (VB-Audio Virtual Cable)**.
3. Click **Let's Check** and talk. You should hear the effect.
4. For the cleanest result, turn off Discord's *Noise Suppression* and *Echo Cancellation*.
   They are designed for natural speech and can smear heavy effects.

Other apps work the same way: choose *CABLE Output* as the microphone in OBS (Audio Input
Capture), in your game's voice settings, and so on.

To hear yourself while you test, tick **Hear myself** and choose your headphones under
*Monitor*. Don't pick speakers, or your mic will pick the sound back up.

## AI Voice

AI Voice converts your voice into another person's voice in real time, using
[RVC](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI) v2 models through
ONNX Runtime with DirectML. It runs after mic enhancement and before the voice effects, so you
can still add Robot, Radio and the other effects on top.

**Requirements**
- A DirectX 12 GPU. A discrete GPU is strongly recommended. Voice CTX automatically picks the
  GPU with the most video memory (for example the RTX card rather than the Intel iGPU on a
  laptop). Without a GPU it falls back to the CPU, which is usually too slow; when that happens
  you hear your normal voice and the status line says so.
- About 1 GB of video memory, and about 800 MB of disk space for the models.

**Delay:** about **180 ms** in addition to the normal ~35 ms. Each 100 ms block takes about 28 ms
on an RTX 4060 Laptop GPU. That delay is fine for voice chat and streaming, but you'll notice it
if you monitor yourself.

### Install the models (once)

The models are too large to ship inside the exe. They live in `%AppData%\VoiceCtx\AiVoice\`.
To build them from pinned, checksummed upstream sources:

```powershell
cd tools/aivoice
python -m venv .venv; .venv\Scripts\activate
pip install -r requirements.txt
python build_models.py          # writes %AppData%\VoiceCtx\AiVoice
```

This downloads the content encoder and the RMVPE pitch model from the RVC project, plus two
stock voices, verifies their SHA-256 checksums, and exports everything to ONNX. You can also
copy a prepared `AiVoice` folder from another PC.

### Use it

1. Open the **AI Voice** tab, pick a voice and switch it **ON** (or press **Ctrl+Alt+A**).
2. Set **Pitch shift** to match the voice to your range. Use +12 if you have a deeper voice and
   pick a female voice, and −12 the other way round.

### Add more voices

RVC voices are usually shared as `.pth` files. Convert one with:

```powershell
python tools/aivoice/convert_voice.py my_voice.pth --name "My character" --gender female --pitch-shift 12
```

Then click **Refresh** in the AI Voice tab. To bring in a voice already converted on another
PC, use **Import voice…** and select its `.onnx` file; the matching `.json` must be next to it.
Only RVC **v2** voices with pitch guidance are supported.

The converter loads checkpoints with `torch.load(weights_only=True)`, so a malicious `.pth` file
cannot run code. Voice CTX itself never reads `.pth` files.

> **Only use voices you have the rights to.** Many RVC voices online are clones of real people
> made without their consent. Don't use AI voices to impersonate anyone or to deceive people.

### Built-in voices and credits

| Voice | Source |
|---|---|
| Narrator (male) | VCTK speaker p226 |
| Presenter (female) | VCTK speaker p231 |

Both voices are trained on the CSTR VCTK Corpus (Centre for Speech Technology Research,
University of Edinburgh), licensed CC BY 4.0. The RVC models are by Nekochu
([RVC-VCTK_Voice-sample](https://huggingface.co/Nekochu/RVC-VCTK_Voice-sample)), Apache-2.0.

The other components:
- **Content encoder:** HuBERT base (MIT), distributed by the RVC project.
- **Pitch model:** RMVPE (MIT), distributed by the RVC project.

The full credits are written to `CREDITS.txt` in the model folder; the **Credits** button opens it.

## Publish a single exe

```powershell
dotnet publish src/VoiceCtx.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The output is one self-contained, compressed `VoiceCtx.App.exe` (about 70 MB) in
`src/VoiceCtx.App/bin/Release/net8.0-windows/win-x64/publish/`. Copy it anywhere and run it.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Discord hears silence | Windows can mute the cable itself. Open **Settings → System → Sound → All sound devices → CABLE Input** and unmute it. The status bar in Voice CTX warns when the output device is muted. |
| "VB-Audio Virtual Cable not found" | Install it from https://vb-audio.com/Cable/, then restart Voice CTX (a reboot may be needed after the driver install). |
| Robotic or crackling audio | Close other heavy audio apps. In the **Advanced** tab of the mic and cable device properties, use the same sample rate (48 kHz) for both. |
| Delay feels long | Passthrough adds about 30–40 ms; noise cancellation adds about 20 ms; pitch-based effects add about 30 ms more. |
| AI Voice switch is greyed out | The AI Voice models aren't installed. See [Install the models](#install-the-models-once). |
| "Your PC can't keep up" in AI Voice | The GPU is too slow or busy, so the voice falls back to normal for a few seconds. Close GPU-heavy apps, or check the status line shows your discrete GPU rather than the CPU. |
| Noise cancellation is greyed out | `rnnoise.dll` failed to load. Rebuild it (see below) or reinstall. |
| Hotkeys do nothing | Another app already uses Ctrl+Alt+V, Ctrl+Alt+N or Ctrl+Alt+A. The status bar says which one. |

## How it works

```
mic ──WASAPI capture (10 ms, MMCSS)──► noise cancel ──► studio chain ──► AI voice* ──► effect processor ──► volume ──► FIFO + drift-correcting resampler ──► WASAPI render ──► CABLE Input
                                                                     └──► FIFO + resampler ──► WASAPI render ──► monitor headphones
```

- `src/VoiceCtx.Audio`: audio I/O and DSP, with no UI dependencies.
  - `Engine/`: allocation-free WASAPI shared-mode capture and render loops, a lock-free
    single-producer/single-consumer FIFO, clock-drift compensation, and device fallback and
    recovery.
  - `Dsp/`: `IVoiceEffect`, the pitch shifter, the resampler and the filters.
  - `Effects/`: the effects, plus the crossfading `EffectProcessor`.
  - `Enhancement/`: the RNNoise wrapper (resampled to 48 kHz and framed into 10 ms blocks) and
    the studio vocal chain.
  - `Native/rnnoise.dll`: built from the pinned upstream RNNoise v0.2 release by
    `native/build-rnnoise.ps1`. That script needs the Visual Studio C++ build tools and verifies
    the source tarball's SHA-256. You only need to run it to rebuild the DLL.
  - `AiVoice/`: the RVC streaming pipeline.
    - **Hand-off:** the audio thread passes audio through lock-free FIFOs to an inference worker
      thread, which processes 100 ms blocks with 600 ms of context: 16 kHz → HuBERT features →
      RMVPE pitch (log-mel computed in C#) → voice generator → crossfade → back to the device rate.
    - **When inference is too slow:** the audio thread never waits for it. Instead it fades to your
      normal voice and backs off for a few seconds.
    - **GPU selection:** the GPU is picked through DXGI.
- `tools/aivoice/`: Python tooling that builds and converts AI Voice models. It isn't needed to
  run the app.

\* AI Voice adds about 180 ms. The other stages add only a few milliseconds.
- `src/VoiceCtx.App`: the WPF UI (MVVM), tray icon, global hotkey and settings.
- `tests/VoiceCtx.Tests`: xUnit tests. For every effect they check range, NaN/Inf, reset and
  allocations. They also check that Silence outputs zeros and None is bit-identical, that the
  pitch shifter hits the expected frequency, and they cover click-free switching, drift
  control, format conversion and settings.

The audio thread never locks or allocates. The UI changes effects and parameters through
volatile fields that the audio thread reads once per 10 ms block.

`docs/research/clownfish-findings.md` covers background research on how an existing voice
changer works, and why Voice CTX uses a virtual cable instead of a system audio-effect
plug-in. Voice CTX is an independent implementation written from scratch.

## License

[MIT](LICENSE)

Third-party: RNNoise © Xiph.Org Foundation, Jean-Marc Valin and others, BSD-3-Clause. See
`src/VoiceCtx.Audio/Native/RNNOISE-LICENSE.txt`, which is also shipped next to the published exe.
ONNX Runtime and DirectML are © Microsoft, MIT license. For the AI Voice models and voices, see
[Built-in voices and credits](#built-in-voices-and-credits).
