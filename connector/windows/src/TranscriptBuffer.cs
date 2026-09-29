namespace CodexVoice;

internal sealed class TranscriptBuffer
{
    private readonly SortedDictionary<long, string> _segments = new();
    private readonly object _gate = new();
    private string _partial = "";
    private string? _finalOverride;

    public void Add(long id, string text)
    {
        lock (_gate)
        {
            _segments[id] = Normalize(text);
            _partial = "";
        }
    }

    public void Refine(long id, string text)
    {
        lock (_gate)
        {
            var refined = Normalize(text);
            if (refined.Length > 0 && _segments.ContainsKey(id)) _segments[id] = refined;
        }
    }

    public void SetFinalOverride(string text)
    {
        lock (_gate)
        {
            var refined = Normalize(text);
            if (refined.Length > 0) _finalOverride = refined;
        }
    }

    public void SetPartial(string text)
    {
        lock (_gate) _partial = Normalize(text);
    }

    public string Preview
    {
        get
        {
            lock (_gate)
                return _finalOverride ?? Join(_segments.Values.Append(_partial));
        }
    }

    public string Final
    {
        get
        {
            lock (_gate) return _finalOverride ?? Join(_segments.Values);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _segments.Clear();
            _partial = "";
            _finalOverride = null;
        }
    }

    private static string Normalize(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Join(IEnumerable<string> parts) => string.Join(" ", parts.Where(p => p.Length > 0)).Trim();
}
