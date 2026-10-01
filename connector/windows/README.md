# Codex Voice for Windows: source package

This is the current local Windows Codex CLI voice prototype, packaged so it builds without the older LiveTranscribeRu desktop project. It transcribes with Vosk and Whisper on the PC, lets you edit the text, and sends to a selected **visible** Codex CLI terminal in CMD or PowerShell. The desktop account link opens the separate CLI account page. QR pairing, encrypted live phone history, phone audio transcription on this PC and explicit text sends are included in source, but they need the separate CLI service at `cli.aiqoo.ru`; they cannot work until that service is deployed and the device is paired. A phone send also requires this PC to be unlocked and the exact terminal tab visible. Live phone-to-Codex delivery has not yet been tested.

The local sender checks the selected window, terminal and exact Codex session before keyboard input, and watches that session's rollout afterward. Windows `SendInput` alone is not a delivery receipt. A changed focus or tab can leave the result uncertain; the app retains a recoverable draft and does not retry an uncertain send automatically. Long-dictation delivery was confirmed on the local Windows prototype after Enter closed the overlay and the message appeared in the selected Codex session. The prototype does not need a shared app-server, plugin, or restart of an existing Codex tab.

Text dictated into an active Codex question arrives as a structured question answer. The receipt check recognizes the exact answer in a newly written user record for the selected session; mentioning it in question metadata or an assistant quote does not count. It remains a question answer. Before inserting keys, the sender also checks the Windows integrity level of the recipient process. Windows blocks input into a process with higher privileges, and the connector preserves the draft with a specific explanation.

## Requirements

- Windows x64, .NET 8 SDK for building, microphone access, and a signed-in Codex CLI. This package has been built on Windows; other operating systems are not supported.
- Two local models, downloaded separately. No model files or weights are in this repository.
- A visible Codex CLI tab in CMD or PowerShell. Hidden Windows Terminal tabs cannot be enumerated from a window title.

## Set up models

Create `%LOCALAPPDATA%\CodexVoice\models` or another directory you control. Put these exact paths inside it:

```text
models/
  vosk-model-small-ru-0.22/
    conf/ ...
  ggml-largev3turbo-q5_0.bin
```

1. Download and extract [Vosk's `vosk-model-small-ru-0.22` ZIP](https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip). Keep the extracted directory name. [Vosk's model catalog](https://alphacephei.com/vosk/models) lists this Russian model and its Apache 2.0 license.
2. Download [whisper.cpp's `ggml-large-v3-turbo-q5_0.bin`](https://huggingface.co/ggerganov/whisper.cpp/blob/main/ggml-large-v3-turbo-q5_0.bin). Rename the downloaded file to **`ggml-largev3turbo-q5_0.bin`** (the prototype's filename, without the extra hyphens). The linked file page publishes SHA-256 `394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2`; verify with `Get-FileHash` if desired.

The optional `silero_vad.onnx` and `speaker-embedding.onnx` files are not required for microphone dictation and are not included. The ASR code creates editable `terms.txt` and `vocabulary.txt` defaults beside the built executable when it can write there. Recognition reads the required models relative to the selected models directory. Keep downloaded weights out of Git; the repository ignore rules cover the names above and common model extensions.

## Build and run

From the repository root in PowerShell:

```powershell
dotnet build .\connector\windows\CodexVoice.csproj -c Release
.\connector\windows\Start-CodexVoice.ps1
```

For another model directory, run `Start-CodexVoice.ps1 -ModelsRoot 'D:\MyModels'`. The launcher starts only the hidden tray app and refuses a second process from the same build. It does not start or modify Codex. To inspect the UI without a microphone or sending text, run `dotnet .\connector\windows\bin\Release\net8.0-windows\CodexVoice.dll --preview`. Run deterministic local checks with `dotnet .\connector\windows\bin\Release\net8.0-windows\CodexVoice.dll --self-test`.

`src/` contains the connector UI, session identification, delivery, CLI account pairing, encrypted history reader, playback and local tests. `asr/` contains seven extracted audio/recognition source files. The package has no project reference to LiveTranscribeRu. Some earlier direct app-server helpers remain compiled for protocol tests, but the current desktop send path uses the foreground terminal. The legacy AIQOO Core phone relay and its pairing implementation are absent.

The source package has a [Windows installer build](installer/README.md). A local installer with bundled models was built and installed on 2026-10-01; no public binary release has been published yet. The project's source is public for inspection under the repository's [view-only terms](../../LICENSE). See [third-party notices](THIRD_PARTY.md) for the packages and models.
