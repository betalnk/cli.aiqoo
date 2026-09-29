# Archived Codex hook experiment

This directory preserves an earlier hook-based integration for reference. It is not registered as a marketplace plugin and is not part of the current Windows connector. The current [Windows source package](../../connector/windows/) identifies visible Codex CLI sessions without these hooks.

The plugin bundles hooks that run `CodexVoice.exe --register-from-hook` for Codex session start/resume and user prompt submission. Codex queues SessionStart at thread creation and executes it during the first turn; simply opening an empty TUI does not register its ID. The helper reads the exact `session_id` from the hook's JSON on standard input. Registration is idempotent because the prompt hook runs on every user prompt.

The companion executable must be installed and available as `CodexVoice.exe` on `PATH`. Codex must have hooks enabled, and the user must review and trust this plugin's hooks before they run. Installing or enabling the plugin alone does not grant that trust. A session that was open before installation is registered only after its next user prompt or a resume; installation does not trigger a hook in that session immediately.

The hooks only register session IDs. They do not identify the foreground terminal window, capture audio, or deliver a prompt to Codex. The companion handles the hotkey, microphone, local speech recognition and delivery. Its overlay requires visible recipient confirmation before sending. If no verified direct delivery channel is available, recognized text must remain editable and visibly unsent.

The source package is available in this repository; no public installer or released binary is available. This archived hook experiment should not be used as installation guidance. See [current status](../../docs/status.md) for the supported flow.
