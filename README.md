# Voice CTX

A real-time voice changer for Windows. Voice CTX captures your microphone, applies a voice
effect, and sends the result to the VB-Audio Virtual Cable. Discord, games, OBS or any other
app can then use **CABLE Output** as its microphone and hear your changed voice.

- 15 effects: None, Male, Female, Baby, Helium, Custom pitch (−12…+12 semitones), Robot,
  Alien, Atari, Clone, Mutation, Fast mutation, Slow mutation, Radio, Silence
- Real pitch shifting (time-domain WSOLA / granular overlap-add), not a playback-speed change
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
| Hotkey does nothing | Another app already uses Ctrl+Alt+V. The status bar says so when this happens. |
| Robotic or crackling audio | Close other heavy audio apps. In the **Advanced** tab of the mic and cable device properties, use the same sample rate (48 kHz) for both. |
| Delay feels long | Passthrough adds about 30–40 ms; pitch-based effects add about 30 ms more. |

## How it works

```
mic ──WASAPI capture (10 ms, MMCSS)──► effect processor ──► volume ──► FIFO + drift-correcting resampler ──► WASAPI render ──► CABLE Input
                                                                     └──► FIFO + resampler ──► WASAPI render ──► monitor headphones
```

- `src/VoiceCtx.Audio`: audio I/O and DSP, with no UI dependencies.
  - `Engine/`: allocation-free WASAPI shared-mode capture and render loops, a lock-free
    single-producer/single-consumer FIFO, clock-drift compensation, and device fallback and
    recovery.
  - `Dsp/`: `IVoiceEffect`, the pitch shifter, the resampler and the filters.
  - `Effects/`: the effects, plus the crossfading `EffectProcessor`.
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
