# Windows installer

`Build-Installer.ps1` publishes the current Windows client as a self-contained x64 application and builds an Inno Setup installer. The installer contains the local Whisper and Vosk weights, so a new installation can start without a separate model download or .NET runtime installation. It installs for the current Windows user, adds Start Menu and desktop shortcuts, and does not close a running Codex Voice or Codex CLI process.

Requirements: .NET 8 SDK, Inno Setup 6, and the two model files described in [the Windows client README](../README.md). Pass the directory containing `ggml-largev3turbo-q5_0.bin` and `vosk-model-small-ru-0.22`:

```powershell
& .\connector\windows\installer\Build-Installer.ps1 -ModelsRoot 'D:\CodexVoice\models'
```

The resulting `dist/windows/installer/CodexVoice-Setup-<version>.exe` includes an uninstall entry. The script prints its path, byte size, and SHA-256. `dist/` is ignored by Git. The owner chose GitHub Releases for the large compiled file; installer source stays in Git. The [v0.1.0 installer](https://github.com/betalnk/cli.aiqoo/releases/tag/v0.1.0) and its checksum are published there. The version comes from `CodexVoice.csproj`; `connector/windows/version.json` is updated only after a tested public release exists.

For a silent per-user install:

```powershell
& .\dist\windows\installer\CodexVoice-Setup-0.1.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

The shortcut passes `--models-root` to the installed `models` directory. The setup includes the project's source terms, third-party overview, and [third-party license texts](notices/README.md) in `notices/`. For future releases, build the binary from the source commit named by its tag.
