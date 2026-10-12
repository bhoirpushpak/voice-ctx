# Third-party notices

Voice CTX is MIT-licensed (see [LICENSE](LICENSE)). It includes or uses the following
third-party software and data. Their full license texts are in the [licenses/](licenses) folder,
which is also shipped in the release download.

## Bundled in VoiceCtx.exe

| Component | Version | License | Notes |
|---|---|---|---|
| [NAudio](https://github.com/naudio/NAudio) (Core, Wasapi) | 2.2.1 | MIT, © Mark Heath | Windows audio (WASAPI) access |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) | 1.20.1 | MIT, © Microsoft Corporation | AI Voice inference |
| [DirectML](https://github.com/microsoft/DirectML) | 1.15.2 | Microsoft Software License Terms (redistributable) | GPU acceleration for AI Voice |
| [RNNoise](https://github.com/xiph/rnnoise) | 0.2 | BSD-3-Clause, © Xiph.Org Foundation, Jean-Marc Valin and others | Noise cancellation, built from source by `native/build-rnnoise.ps1` |
| .NET 8 runtime (self-contained build) | 8.0 | MIT, © .NET Foundation | Included in the single-file exe |

## AI Voice model pack (separate optional download)

| Component | License | Source |
|---|---|---|
| Content encoder: HuBERT base, exported to ONNX | MIT | Distributed by the RVC project, [lj1995/VoiceConversionWebUI](https://huggingface.co/lj1995/VoiceConversionWebUI) |
| RMVPE pitch estimator | MIT | Distributed by the RVC project, [lj1995/VoiceConversionWebUI](https://huggingface.co/lj1995/VoiceConversionWebUI) |
| Voice architecture: Retrieval-based Voice Conversion (RVC) v2 | MIT | [RVC-Project](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI) |
| Built-in voices "Narrator (male)" (VCTK p226) and "Presenter (female)" (VCTK p231) | Data: CC BY 4.0. Models: Apache-2.0 | Trained on the CSTR VCTK Corpus (Centre for Speech Technology Research, University of Edinburgh), [datashare.ed.ac.uk/handle/10283/3443](https://datashare.ed.ac.uk/handle/10283/3443). RVC models by Nekochu, [RVC-VCTK_Voice-sample](https://huggingface.co/Nekochu/RVC-VCTK_Voice-sample) |

The model pack contains its own `CREDITS.txt`, which the app's **Credits** button opens.

## Optional: VB-Audio Virtual Cable

VB-Audio Virtual Cable is **not** included. It is a separate free product from VB-Audio Software
([vb-audio.com/Cable](https://vb-audio.com/Cable/)) that each user installs themselves.
