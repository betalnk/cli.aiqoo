namespace CodexVoice;

internal static class VoiceDraftStoreSelfTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexVoiceDraftTest-" + Guid.NewGuid().ToString("N"));
        var store = new VoiceDraftStore(root);
        var originalId = Guid.NewGuid().ToString("D");
        var selectedId = Guid.NewGuid().ToString("D");
        var selected = new CapturedSession(new IntPtr(1234), "Codex | saved tab",
            "saved tab", "project", selectedId, 42, 99);
        try
        {
            var draft = VoiceDraft.Capture(originalId, selected, "первый текст", "Ожидает уточнения");
            store.Save(draft);
            var loaded = store.Load(originalId)
                ?? throw new InvalidOperationException("Draft was not loaded.");
            Check(loaded.OriginalThreadId == originalId);
            Check(loaded.Recipient.ToCapturedSession() == selected);
            Check(loaded.Text == "первый текст");

            store.Save(draft.Update("уточнённый текст", "Получение Codex неизвестно"));
            var recovered = store.LoadNewest()
                ?? throw new InvalidOperationException("Latest draft was not recovered.");
            Check(recovered.OriginalThreadId == originalId);
            Check(recovered.Recipient.ThreadId == selectedId);
            Check(recovered.Text == "уточнённый текст");
            Check(recovered.Status == "Получение Codex неизвестно");

            // A new activation keeps its own recipient even when another session
            // has the newest saved draft. Meaningful text remains recoverable.
            Check(store.LoadForActivation(Guid.NewGuid().ToString("D")) is null);
            Check(store.LoadForActivation(originalId) == recovered);
            var emptyId = Guid.NewGuid().ToString("D");
            var empty = VoiceDraft.Capture(emptyId, selected, " \r\n\t", "Сохранён для проверки");
            store.Save(empty);
            Check(store.LoadForActivation(emptyId) is null);
            Check(store.Load(emptyId) == empty);
            Check(store.LoadForActivation(originalId) == recovered);
            store.Delete(emptyId);

            // Fresh activation must neither restore an uncertain message nor replace
            // the current captured session with its older, different recipient.
            var oldBytes = File.ReadAllBytes(Path.Combine(root, originalId + ".json"));
            Check(store.PreserveForFreshCapture(selectedId) is null);
            Check(store.Load(originalId) == recovered);
            var preserved = store.PreserveForFreshCapture(originalId)
                ?? throw new InvalidOperationException("Earlier draft was not preserved.");
            Check(File.ReadAllBytes(preserved).SequenceEqual(oldBytes));
            Check(store.Load(originalId) is null);
            Check(store.LoadForActivation(originalId) is null);
            Check(store.LoadNewest() is null);
            Check(store.PreserveForFreshCapture(originalId) is null);

            // A new recording may be saved and received without deleting the
            // preserved message or turning the next activation into recovery.
            var current = VoiceDraft.Capture(originalId, selected, "новая запись", "Ожидает отправки");
            store.Save(current);
            Check(store.Load(originalId) == current);
            store.Delete(originalId);
            Check(File.Exists(preserved));
            Check(File.ReadAllBytes(preserved).SequenceEqual(oldBytes));

            store.Save(empty);
            var preservedEmpty = store.PreserveForFreshCapture(emptyId);
            Check(preservedEmpty is not null && File.Exists(preservedEmpty));
            Check(store.Load(emptyId) is null);
        }
        finally
        {
            var fullRoot = Path.GetFullPath(root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            if (!fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullRoot).StartsWith("CodexVoiceDraftTest-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected draft test cleanup path.");
            if (Directory.Exists(fullRoot)) Directory.Delete(fullRoot, recursive: true);
        }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Voice draft self-test failed.");
    }
}
