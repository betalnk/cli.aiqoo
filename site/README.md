# CLI site

Standalone static draft for the proposed `cli.aiqoo.ru`. It has no dependency on the AIQOO web application or Core account. Serve this directory with any static server. For local preview from the repository root:

```powershell
python -m http.server 4178 --directory .\site
```

The landing page and docs describe the local prototype. `login.html` starts independent GitHub sign-in through `/api/v1`; `account.html` reads the CLI profile, connected devices and an encrypted live view of available Codex sessions and their three latest messages. It opens `/api/v1/ws/history` while visible and decrypts snapshots with the browser key saved during pairing. A response can be read aloud on demand when the browser exposes a local voice for its language; playback never starts automatically. The selected online session also has a text composer. Phone microphone capture produces local PCM and a WAV preview; an explicit Recognize action sends encrypted PCM to the linked PC for local ASR, and the returned transcript remains editable until an explicit Send. A Send uses a fresh encrypted command and waits for an exact-session desktop receipt; a lost route is shown as uncertain and is never retried automatically. The Windows sender currently accepts only one line without shell metacharacters or emoji, and the browser checks this before sending. `pair.html` reads a QR fragment, generates the browser key and waits for desktop confirmation. A static server can preview layout but cannot complete sign-in, pairing, live history or remote commands without the separate private API, provider credentials and same-origin routing. No production deployment has been made.

Dependency-free tests cover history/session transitions, AES-GCM command frames and acknowledgements, per-session drafts and local PCM/WAV conversion:

```powershell
node .\site\tests\workspace-smoke.mjs
node .\site\tests\command-smoke.mjs
node .\site\tests\compose-smoke.mjs
node .\site\tests\audio-capture-smoke.mjs
```
