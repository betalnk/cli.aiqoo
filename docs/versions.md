# Windows version feed

The installed Windows connector reads `connector/windows/version.json` from GitHub `main` at startup and on a periodic timer. This is a small metadata request. It does not transmit microphone audio, recognized text, session IDs or account details. Network errors leave voice input available. The user can check manually from the tray.

The feed version changes only when a tested Windows build has been published. Changes to the website or documentation do not count as an application update. A higher version must include `releaseUrl` pointing to that build's release in this repository; the connector opens that page only when the user chooses to view it. It does not download or install updates automatically.

The current `0.1.0` entry identifies the local prototype and has no release URL. It must not trigger an update notice. Before publishing the first binary, verify the build, create the release, then update the manifest in `main` to the released version and URL. If a later main commit has unreleased changes, leave the manifest at the last available version.
