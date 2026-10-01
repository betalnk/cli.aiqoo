# Third-party components

This document records direct dependencies for the Windows source build. `dotnet restore` downloads NuGet packages; their binaries are not committed here. The package metadata at the pinned versions identifies these licenses:

| Component | Version | License and upstream |
| --- | --- | --- |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent | 11.2.3 | [MIT](https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md) |
| Avalonia.Fonts.Inter package | 11.2.3 | NuGet package declares MIT; the [Inter font](https://github.com/rsms/inter) itself is SIL Open Font License 1.1 |
| [NAudio](https://github.com/naudio/NAudio) | 2.2.1 | MIT (`license.txt` in the NuGet package) |
| [QRCoder](https://github.com/Shane32/QRCoder) | 1.8.0 | MIT (pinned NuGet package metadata) |
| [Microsoft.ML.OnnxRuntime](https://github.com/microsoft/onnxruntime) | 1.20.1 | MIT (`LICENSE` in the NuGet package) |
| [Vosk](https://alphacephei.com/vosk/) | 0.3.38 | Apache 2.0 (NuGet metadata) |
| [Whisper.net](https://github.com/sandrohanea/whisper.net), Whisper.net.Runtime, Whisper.net.Runtime.Cuda | 1.8.1 | MIT (`LICENSE` in the NuGet package and runtime package metadata) |

Model files are downloaded separately for a source build. The Windows installer build bundles the local copies supplied to `Build-Installer.ps1`:

| Model | Source and upstream license |
| --- | --- |
| `vosk-model-small-ru-0.22` | [Vosk model catalog](https://alphacephei.com/vosk/models), Apache 2.0 |
| `ggml-largev3turbo-q5_0.bin` (renamed from upstream) | [ggerganov/whisper.cpp model page](https://huggingface.co/ggerganov/whisper.cpp/blob/main/ggml-large-v3-turbo-q5_0.bin), repository model card marked MIT |

The Windows installer includes the applicable license and third-party notice texts in `notices/licenses/`; their versions and sources are recorded in [the installer notice manifest](installer/notices/README.md). This file does not choose a license for the Codex Voice source.
