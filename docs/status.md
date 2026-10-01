# Current status

| Area | Status |
| --- | --- |
| Windows voice input for Codex CLI | Buildable source package and [installer script](../connector/windows/installer/README.md). A bundled-model installer passed a local install and self-test on 2026-10-01; no public binary release yet. |
| Codex plugin | An [archived hook experiment](../archive/legacy-codex-plugin/) is preserved for reference. The current local Windows flow does not require hooks, plugin trust, a shared app-server, or restarting an open Codex tab. |
| Local Vosk/Whisper recognition and editable text | Implemented in the local prototype; recognition quality and full user flow still need device testing. |
| Selected-session delivery | The current local prototype discovers visible Codex CLI tabs in CMD/PowerShell, matches the selected tab to an exact session, then validates its window, tab and session again before sending keys. The owner confirmed short and long messages with the `[via Voice Connector]` prefix reached the selected live session, and the overlay closed immediately after Enter. A Windows `SendInput` result is not a Codex receipt; the prototype separately checks the selected session rollout. |
| Local playback of selected-session AI answers | Implemented in the local prototype; full user flow has not been verified. |
| Windows version check | Implemented locally. The `main` feed is part of this source repository but has no public release link yet; it will not advertise an untested binary. |
| Phone interface, remote sessions, independent sign-in | Login/account/pairing, live three-message history, editable text, phone PCM recording, PC-local ASR and explicit encrypted sends are implemented in source. The separate private OAuth, pairing, history and one-shot command relay pass offline tests; the Windows package builds and passes self-tests. The PC must be unlocked and the exact Codex CLI tab visible for a phone send. Real GitHub OAuth, HTTPS deployment, iPhone audio and a full phone-to-PC delivery test remain pending. |
| Claude Code, macOS, Linux | Planned; no verified support. |

The website is a static draft. Its diagrams are conceptual and are not screenshots of a released application.
