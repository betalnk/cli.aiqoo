# Bundled license and notice texts

These files are copied into the installed application's `notices/licenses/` directory. `THIRD_PARTY.md` identifies the direct NuGet packages and bundled model weights. The texts here cover their licenses and the notices carried by transitive/native dependencies in the Windows publish output.

| Files | Source for this release |
| --- | --- |
| `Apache-2.0.txt` | [Apache Software Foundation license text](https://www.apache.org/licenses/LICENSE-2.0.txt), for Vosk 0.3.38 and `vosk-model-small-ru-0.22` |
| `Avalonia-LICENSE.txt`, `Avalonia-NOTICE.txt` | [Avalonia 11.2.3 license](https://github.com/AvaloniaUI/Avalonia/blob/11.2.3/licence.md) and [notices](https://github.com/AvaloniaUI/Avalonia/blob/11.2.3/NOTICE.md), including Avalonia.Fonts.Inter package terms |
| `Inter-OFL.txt` | [Inter font license](https://github.com/rsms/inter/blob/master/LICENSE.txt), for the font shipped by Avalonia.Fonts.Inter |
| `OpenAI-Whisper-LICENSE.txt`, `WhisperCpp-LICENSE.txt` | [OpenAI Whisper license](https://github.com/openai/whisper/blob/main/LICENSE) and [whisper.cpp license](https://github.com/ggml-org/whisper.cpp/blob/master/LICENSE), for the bundled converted Whisper model |
| `Avalonia-Angle-Windows-Natives-LICENSE.txt` | NuGet package `Avalonia.Angle.Windows.Natives` 2.1.22045.20230930 `LICENSE` |
| `HarfBuzzSharp-LICENSE.txt` | NuGet package `HarfBuzzSharp` 7.3.0.3 `LICENSE.txt`, including its native asset packages |
| `NAudio-LICENSE.txt` | NuGet package `NAudio` 2.2.1 `license.txt`, including NAudio subpackages |
| `OnnxRuntime-LICENSE.txt`, `OnnxRuntime-ThirdPartyNotices.txt` | NuGet package `Microsoft.ML.OnnxRuntime` 1.20.1 `LICENSE` and `ThirdPartyNotices.txt` |
| `QRCoder-LICENSE.txt` | NuGet package `QRCoder` 1.8.0 `LICENSE.txt` |
| `SkiaSharp-LICENSE.txt` | NuGet package `SkiaSharp` 2.88.9 `LICENSE.txt`, including its native asset packages |
| `WhisperNet-LICENSE.txt` | NuGet package `Whisper.net` 1.8.1 `LICENSE`, including Whisper.net runtime packages |
| `DotNetRuntime-LICENSE.txt`, `DotNetRuntime-ThirdPartyNotices.txt` | NuGet package `Microsoft.NETCore.App.Runtime.win-x64` 8.0.31 `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT`, for the self-contained runtime |

When dependency or runtime versions change, update these texts from the versions actually distributed before the next release.
