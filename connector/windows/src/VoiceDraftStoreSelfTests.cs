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

            store.Delete(originalId);
            Check(store.Load(originalId) is null);
            Check(store.LoadNewest() is null);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Voice draft self-test failed.");
    }
}
