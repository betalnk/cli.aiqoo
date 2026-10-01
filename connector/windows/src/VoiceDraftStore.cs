using System.Text.Json;

namespace CodexVoice;

internal sealed record VoiceDraftRecipient(
    long WindowHandle, string WindowTitle, string Name, string Project, string ThreadId,
    uint WindowProcessId, long WindowProcessStartTicks)
{
    internal static VoiceDraftRecipient Capture(CapturedSession session) => new(
        session.Window.ToInt64(), session.WindowTitle, session.Name, session.Project,
        session.ThreadId, session.WindowProcessId, session.WindowProcessStartTicks);

    internal CapturedSession ToCapturedSession() => new(
        new IntPtr(WindowHandle), WindowTitle, Name, Project, ThreadId,
        WindowProcessId, WindowProcessStartTicks);
}

internal sealed record VoiceDraft(
    string OriginalThreadId, VoiceDraftRecipient Recipient, string Text,
    string Status, DateTimeOffset UpdatedAtUtc)
{
    internal static VoiceDraft Capture(CapturedSession original, CapturedSession recipient,
        string text, string status) => Capture(original.ThreadId, recipient, text, status);

    internal static VoiceDraft Capture(string originalThreadId, CapturedSession recipient,
        string text, string status) => new(originalThreadId,
            VoiceDraftRecipient.Capture(recipient), text, status, DateTimeOffset.UtcNow);

    internal VoiceDraft Update(string text, string status) => this with
    {
        Text = text,
        Status = status,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };
}

internal sealed record BackgroundSendRequest(
    VoiceDraft Draft, Func<Task<string>> ResolveText, Func<bool> HasMicrophoneFailure,
    Func<Task>? CancelSpeechOnFailure, bool RecipientConfirmed, Task OverlayClosed);

/// <summary>One atomic, per-user draft for each UUID from which the overlay was opened.</summary>
internal sealed class VoiceDraftStore
{
    private const int MaxDraftBytes = 1024 * 1024;
    private readonly string _root;

    internal VoiceDraftStore(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexVoice", "drafts");
    }

    internal VoiceDraft? Load(string originalThreadId)
    {
        var path = PathFor(originalThreadId);
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length > MaxDraftBytes)
            throw new InvalidDataException("Сохранённый черновик слишком велик.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read);
        var draft = JsonSerializer.Deserialize<VoiceDraft>(file)
            ?? throw new InvalidDataException("Сохранённый черновик пуст.");
        Validate(draft);
        if (!string.Equals(draft.OriginalThreadId, originalThreadId,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UUID сохранённого черновика не совпадает с файлом.");
        return draft;
    }

    internal VoiceDraft? LoadNewest()
    {
        if (!Directory.Exists(_root)) return null;
        var newest = Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        return newest is null ? null : Load(Path.GetFileNameWithoutExtension(newest));
    }

    /// <summary>Recover text from this session without redirecting a fresh capture to another tab.</summary>
    internal VoiceDraft? LoadForActivation(string originalThreadId)
    {
        var draft = Load(originalThreadId);
        // Closing an unused overlay can leave an empty draft. It must not turn the
        // next activation into recovery mode and suppress microphone startup.
        return draft is not null && !string.IsNullOrWhiteSpace(draft.Text) ? draft : null;
    }

    /// <summary>Preserve an earlier message; a hotkey must never restore it implicitly.</summary>
    internal string? PreserveForFreshCapture(string originalThreadId)
    {
        var path = PathFor(originalThreadId);
        if (!File.Exists(path)) return null;
        var preservedRoot = Path.Combine(_root, "preserved");
        Directory.CreateDirectory(preservedRoot);
        var preserved = Path.Combine(preservedRoot,
            $"{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.json");
        File.Move(path, preserved); // Same-volume rename; never overwrite an older copy.
        return preserved;
    }

    internal void Save(VoiceDraft draft)
    {
        Validate(draft);
        Directory.CreateDirectory(_root);
        var path = PathFor(draft.OriginalThreadId);
        var temporary = Path.Combine(_root, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, draft);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal void Delete(string originalThreadId)
    {
        var path = PathFor(originalThreadId);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string threadId)
    {
        if (!SessionResolver.TryCanonicalThreadId(threadId, out var canonical))
            throw new InvalidDataException("UUID исходной сессии Codex неверен.");
        return Path.Combine(_root, canonical + ".json");
    }

    private static void Validate(VoiceDraft draft)
    {
        if (draft.Recipient is null
            || !SessionResolver.TryCanonicalThreadId(draft.OriginalThreadId, out _)
            || !SessionResolver.TryCanonicalThreadId(draft.Recipient.ThreadId, out _)
            || draft.Text is null || draft.Text.Length > 100_000
            || draft.Recipient.WindowTitle is null || draft.Recipient.Name is null
            || draft.Recipient.Project is null || draft.Status is null)
            throw new InvalidDataException("Сохранённый черновик повреждён.");
    }
}
