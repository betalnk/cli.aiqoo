using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexVoice;

internal sealed record BoundSession(string ThreadId, string Name, string Cwd, DateTimeOffset BoundAtUtc,
    string? Source = null)
{
    [JsonIgnore]
    internal string Project => Path.GetFileName(Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}

internal delegate bool ExactSessionLookup(string threadId, out SessionIdentity? identity, out string error);

/// <summary>Per-user Codex session registrations from plugin hooks and legacy manual diagnostics.</summary>
internal sealed class SessionBindingStore
{
    private const int MaxBindings = 32;
    private const int MaxFileBytes = 64 * 1024;
    private static readonly TimeSpan BindingLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan BootstrapLifetime = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly ExactSessionLookup _lookup;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly string _mutexName;

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexVoice", "session-bindings.json");

    internal SessionBindingStore(string? path = null, ExactSessionLookup? lookup = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _path = path ?? DefaultPath;
        _lookup = lookup ?? SessionResolver.TryResolveExactThread;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        var pathHash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_path).ToUpperInvariant()));
        _mutexName = "Local\\CodexVoiceBindings_" + Convert.ToHexString(pathHash.AsSpan(0, 8));
    }

    internal bool TryBind(string? inheritedThreadId, out BoundSession? binding, out string error)
    {
        binding = null;
        if (!SessionResolver.TryCanonicalThreadId(inheritedThreadId, out var canonical))
        {
            error = "CODEX_THREAD_ID отсутствует или имеет неверный формат UUID.";
            return false;
        }

        if (!_lookup(canonical, out var identity, out error) || identity is null ||
            !string.Equals(identity.ThreadId, canonical, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(identity.Name) || string.IsNullOrWhiteSpace(identity.Cwd))
        {
            if (string.IsNullOrWhiteSpace(error)) error = "Точная сессия Codex не подтверждена.";
            return false;
        }

        binding = new BoundSession(canonical, identity.Name, identity.Cwd, _utcNow(), "manual");
        if (TryStore(binding, out error)) return true;
        binding = null;
        return false;
    }

    /// <summary>Registers the exact session ID supplied by a trusted Codex plugin hook.</summary>
    internal bool TryRegisterHookPayload(string? payload, out string error)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > 1024 * 1024)
        {
            error = "Hook payload is empty or too large.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("hook_event_name", out var eventProperty) ||
                eventProperty.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("session_id", out var sessionProperty) ||
                sessionProperty.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("cwd", out var cwdProperty) ||
                cwdProperty.ValueKind != JsonValueKind.String)
            {
                error = "Hook payload is missing required fields.";
                return false;
            }

            var eventName = eventProperty.GetString();
            if (eventName == "SessionStart")
            {
                if (!root.TryGetProperty("source", out var sourceProperty) ||
                    sourceProperty.ValueKind != JsonValueKind.String ||
                    sourceProperty.GetString() is not ("startup" or "resume"))
                {
                    error = "Unsupported SessionStart source.";
                    return false;
                }
            }
            else if (eventName != "UserPromptSubmit")
            {
                error = "Unsupported hook event.";
                return false;
            }

            if (!SessionResolver.TryCanonicalThreadId(sessionProperty.GetString(), out var threadId))
            {
                error = "Hook session ID is invalid.";
                return false;
            }

            var cwd = cwdProperty.GetString();
            if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd))
            {
                error = "Hook working directory is invalid.";
                return false;
            }

            var binding = new BoundSession(threadId, "(pending)", Path.GetFullPath(cwd),
                _utcNow(), "plugin");
            return TryStore(binding, out error);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            error = "Hook payload is invalid: " + exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Short-lived first-turn identity obtained from a loaded CLI thread and trusted hook preflight.
    /// The first turn's hook replaces it with a normal plugin registration.
    /// </summary>
    internal bool TryRegisterBootstrap(LoadedThreadIdentity identity, out string error)
    {
        if (!SessionResolver.TryCanonicalThreadId(identity.ThreadId, out var canonical)
            || string.IsNullOrWhiteSpace(identity.Cwd) || !Path.IsPathFullyQualified(identity.Cwd))
        {
            error = "Новая сессия Codex не подтверждена.";
            return false;
        }
        var name = string.IsNullOrWhiteSpace(identity.Name) ? "Новая сессия" : identity.Name;
        return TryStore(new BoundSession(canonical, name, Path.GetFullPath(identity.Cwd),
            _utcNow(), "bootstrap"), out error);
    }

    private bool TryStore(BoundSession binding, out string error)
    {
        using var mutex = new Mutex(false, _mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                error = "Хранилище привязок занято. Повторите подключение.";
                return false;
            }

            if (!TryRead(out var previous, out error)) return false;
            var now = _utcNow();
            if (binding.Source == "bootstrap" && previous.Any(item =>
                    item.ThreadId == binding.ThreadId && item.Source == "plugin" && IsFresh(item, now)))
            {
                error = "";
                return true;
            }
            var records = previous
                .Where(item => item.ThreadId != binding.ThreadId && IsFresh(item, now))
                .Append(binding)
                .OrderByDescending(item => item.BoundAtUtc)
                .Take(MaxBindings).ToArray();
            WriteAtomically(records);
            error = "";
            return true;
        }
        catch (Exception exception)
        {
            error = $"Не удалось сохранить привязку: {exception.Message}";
            return false;
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    /// <summary>Fresh hook registrations remain selectable before Codex names or persists a new thread.</summary>
    internal IReadOnlyList<BoundSession> ListConfirmed()
    {
        if (!TryRead(out var stored, out _)) return Array.Empty<BoundSession>();
        var now = _utcNow();
        var confirmed = new List<BoundSession>();
        foreach (var item in stored)
        {
            if (item.Source is not ("plugin" or "bootstrap") || !IsFresh(item, now)
                || item.Source == "bootstrap" && now - item.BoundAtUtc > BootstrapLifetime) continue;
            if (_lookup(item.ThreadId, out var identity, out _) && identity is not null)
            {
                if (identity.ThreadId != item.ThreadId || string.IsNullOrWhiteSpace(identity.Cwd)
                    || !SameWindowsPath(item.Cwd, identity.Cwd)) continue;
                confirmed.Add(new BoundSession(item.ThreadId,
                    string.IsNullOrWhiteSpace(identity.Name) ? "Новая сессия" : identity.Name,
                    identity.Cwd, item.BoundAtUtc, "plugin"));
            }
            else
            {
                // SessionStart can run before a new, unnamed thread reaches state_5.sqlite.
                // The sender still requires that this exact ID is loaded by the Codex daemon.
                confirmed.Add(item with { Name = "Новая сессия" });
            }
        }
        return confirmed.OrderByDescending(item => item.BoundAtUtc).ToArray();
    }

    internal bool TryGetConfirmed(string? threadId, out BoundSession? binding)
    {
        binding = null;
        if (!SessionResolver.TryCanonicalThreadId(threadId, out var canonical)) return false;
        binding = ListConfirmed().FirstOrDefault(item => item.ThreadId == canonical);
        return binding is not null;
    }

    private bool TryRead(out BoundSession[] records, out string error)
    {
        records = Array.Empty<BoundSession>();
        error = "";
        if (!File.Exists(_path)) return true;
        try
        {
            if (new FileInfo(_path).Length > MaxFileBytes)
                throw new InvalidDataException("Binding store is too large.");
            var file = JsonSerializer.Deserialize<BindingFile>(File.ReadAllBytes(_path), JsonOptions);
            if (file is null || file.Schema != 1 || file.Bindings is null ||
                file.Bindings.Length > MaxBindings || file.Bindings.Any(item =>
                    item is null || !SessionResolver.TryCanonicalThreadId(item.ThreadId, out _) ||
                    string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Cwd) ||
                    !Path.IsPathFullyQualified(item.Cwd) ||
                    item.Source is not (null or "manual" or "plugin" or "bootstrap")))
                throw new InvalidDataException("Binding store format is invalid.");
            records = file.Bindings;
            return true;
        }
        catch
        {
            error = "Файл привязок повреждён или недоступен.";
            return false;
        }
    }

    private void WriteAtomically(BoundSession[] records)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Binding store path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, ".session-bindings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new BindingFile(1, records), JsonOptions);
            if (bytes.Length > MaxFileBytes) throw new InvalidDataException("Binding store is too large.");
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool IsFresh(BoundSession item, DateTimeOffset now)
    {
        var age = now - item.BoundAtUtc;
        return age >= TimeSpan.Zero && age <= BindingLifetime;
    }

    private static bool SameWindowsPath(string left, string right)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed record BindingFile(int Schema, BoundSession[] Bindings);
}
