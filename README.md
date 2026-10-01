# CLI

CLI is an independent project for working with local coding-agent sessions. The current Windows prototype transcribes speech locally, lets you edit the text, and targets a chosen Codex CLI session. The local phone interface code uses a separate account and an encrypted connection to your own PC; it is not deployed yet.

The public website draft is in [`site/`](site/). It is not deployed at `cli.aiqoo.ru` yet. A buildable Windows source package and [installer builder](connector/windows/installer/README.md) are in [`connector/windows/`](connector/windows/); there is no public installer or released binary. See [current status](docs/status.md) before using the project.

## Website preview

From the repository root:

```powershell
python -m http.server 4178 --directory .\site
```

Open `http://localhost:4178/` to review the public pages. The sign-in and account pages call a separate private `/api/v1` service; a static preview alone cannot authenticate users. The CLI service is being developed outside this public repository and has not been deployed.

## Version feed

[`connector/windows/version.json`](connector/windows/version.json) records the Windows connector version advertised by `main`. The local connector checks it when it starts and periodically while running. A new version must point to a tested GitHub release before users are notified. The feed does not install or download software. See [version policy](docs/versions.md).

## Project boundary

Speech recognition runs on the user's PC. A separate private service implements account login, device pairing and an opaque encrypted relay for live history, phone transcription requests and explicit message sends. It neither transcribes audio nor runs Codex commands. This service is outside the public repository and has not been deployed, so phone access is not available yet. The phone send code requires a paired browser, an online unlocked PC and an exact visible Codex CLI session; live end-to-end delivery has not been verified.

The original AIQOO platform and its account system are outside this project. Large speech models, private keys, local session databases, build outputs and the platform source are not part of this repository.

The source is public for inspection under the [view-only terms](LICENSE). All rights are reserved; public access does not grant permission to copy, modify, or redistribute this project's code. Third-party components retain their own licenses. No binary is distributed from this repository yet.
