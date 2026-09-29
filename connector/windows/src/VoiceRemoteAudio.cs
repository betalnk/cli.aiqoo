using System.Security.Cryptography;
using LiveTranscribeRu;

namespace CodexVoice;

/// <summary>One bounded phone PCM stream; all recognition runs in a separate local service.</summary>
internal sealed class VoiceRemoteAudio : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AudioStream? _stream;

    internal event Action<string, string, string>? PartialChanged;

    internal async Task<object> StartAsync(string clientId, string streamId, string format,
        int sampleRate, int channels, CancellationToken token,
        int maxSeconds = 120, int maxChunkBytes = 16 * 1024)
    {
        VoicePairingCrypto.ValidateHexId(clientId, nameof(clientId));
        VoicePairingCrypto.ValidateRequestId(streamId);
        if (format != "pcm_s16le" || channels != 1 || sampleRate is < 8000 or > 96000)
            throw new VoiceAudioException("unsupported_format", "Use mono PCM S16LE at 8–96 kHz.");
        if (maxSeconds is < 1 or > 120 || maxChunkBytes is < 2 or > 16 * 1024
            || (maxChunkBytes & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(maxSeconds));
        await _gate.WaitAsync(token);
        try
        {
            if (_stream is not null)
                throw new VoiceAudioException("audio_busy", "Another phone recording is active on this PC.");
            var state = new AudioStream(clientId, streamId, sampleRate,
                maxSeconds, maxChunkBytes);
            _stream = state;
            state.Service.PartialResult += (_, text) => UpdateDraft(state, () => state.Buffer.SetPartial(text));
            state.Service.SegmentRecognized += (id, _, text, _) =>
                UpdateDraft(state, () => state.Buffer.Add(id, text));
            state.Service.SegmentRefined += (id, text) =>
                UpdateDraft(state, () => state.Buffer.Refine(id, text));
            state.Service.DictationRefined += text =>
                UpdateDraft(state, () => state.Buffer.SetFinalOverride(text));
            try
            {
                await state.Service.StartRemotePcmDictationAsync(sampleRate);
                if (!state.Service.IsRunning || !state.Service.WhisperActive)
                    throw new VoiceAudioException("offline", "Local speech models are unavailable on this PC.");
            }
            catch
            {
                _stream = null;
                try
                {
                    if (state.Service.IsRunning)
                    {
                        state.Service.CancelDictation();
                        await state.Service.StopAsync();
                    }
                }
                catch { }
                throw;
            }
            return new { streamId, accepted = true };
        }
        finally { _gate.Release(); }
    }

    internal async Task<object> ChunkAsync(string clientId, string streamId, int seq,
        string encodedData, CancellationToken token)
    {
        var data = VoicePairingCrypto.DecodeBase64Url(encodedData, 16 * 1024);
        try { return await ChunkBytesAsync(clientId, streamId, seq, data, token); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    internal async Task<object> ChunkBytesAsync(string clientId, string streamId, int seq,
        ReadOnlyMemory<byte> data, CancellationToken token)
    {
        if (data.Length is < 2 or > 16 * 1024 || (data.Length & 1) != 0)
            throw new VoiceAudioException("invalid_request", "PCM chunk must contain complete 16-bit samples.");
        var acquired = false;
        try
        {
            await _gate.WaitAsync(token);
            acquired = true;
            var state = RequireStream(clientId, streamId);
            if (data.Length > state.MaxChunkBytes
                || state.BytesReceived + data.Length > (long)state.SampleRate * 2 * state.MaxSeconds)
                throw new VoiceAudioException("invalid_request", "Phone audio exceeds this command's limits.");
            if (seq < 0 || seq > state.NextSeq)
                throw new VoiceAudioException("invalid_request", "Audio chunks arrived out of order.");
            var digest = SHA256.HashData(data.Span);
            if (seq < state.NextSeq)
            {
                if (!state.ChunkDigests.TryGetValue(seq, out var original) ||
                    !CryptographicOperations.FixedTimeEquals(digest, original))
                    throw new VoiceAudioException("invalid_request", "An audio sequence number was reused with different bytes.");
                return new { accepted = true, seq };
            }
            await state.Service.FeedRemotePcmAsync(data, token);
            state.BytesReceived += data.Length;
            state.ChunkDigests.Add(seq, digest);
            state.NextSeq++;
            return new { accepted = true, seq };
        }
        finally
        {
            if (acquired) _gate.Release();
        }
    }

    internal async Task<object> EndAsync(string clientId, string streamId, int nextSeq,
        CancellationToken token)
    {
        var text = await EndTextAsync(clientId, streamId, nextSeq, token);
        return new { text = text.Length > 16_000 ? text[..16_000] : text };
    }

    internal async Task<string> EndTextAsync(string clientId, string streamId, int nextSeq,
        CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var state = RequireStream(clientId, streamId);
            if (nextSeq != state.NextSeq || nextSeq == 0)
                throw new VoiceAudioException("invalid_request", "Audio stream is incomplete.");
            state.Active = false;
            try
            {
                await state.Service.StopAsync();
                var text = state.Buffer.Final.Trim();
                return text;
            }
            finally { _stream = null; }
        }
        finally { _gate.Release(); }
    }

    internal async Task<object> CancelAsync(string clientId, string streamId)
    {
        await _gate.WaitAsync();
        try
        {
            var state = RequireStream(clientId, streamId);
            state.Active = false;
            _stream = null;
            state.Service.CancelDictation();
            await state.Service.StopAsync();
            return new { canceled = true };
        }
        finally { _gate.Release(); }
    }

    internal async Task CancelAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var state = _stream;
            if (state is null) return;
            state.Active = false;
            _stream = null;
            state.Service.CancelDictation();
            await state.Service.StopAsync();
        }
        finally { _gate.Release(); }
    }

    private AudioStream RequireStream(string clientId, string streamId)
    {
        VoicePairingCrypto.ValidateHexId(clientId, nameof(clientId));
        VoicePairingCrypto.ValidateRequestId(streamId);
        var state = _stream;
        if (state is null || !state.Active || state.ClientId != clientId || state.StreamId != streamId)
            throw new VoiceAudioException("invalid_request", "Phone audio stream is not active.");
        return state;
    }

    private void UpdateDraft(AudioStream state, Action update)
    {
        string text;
        lock (state.BufferGate)
        {
            update();
            text = state.Buffer.Preview;
            if (!state.Active || text == state.LastDraft) return;
            state.LastDraft = text;
            if (text.Length > 4000) text = text[..4000];
        }
        PartialChanged?.Invoke(state.ClientId, state.StreamId, text);
    }

    public async ValueTask DisposeAsync()
    {
        try { await CancelAllAsync(); }
        catch { }
        _gate.Dispose();
    }

    private sealed class AudioStream
    {
        internal AudioStream(string clientId, string streamId, int sampleRate,
            int maxSeconds, int maxChunkBytes)
        {
            ClientId = clientId;
            StreamId = streamId;
            SampleRate = sampleRate;
            MaxSeconds = maxSeconds;
            MaxChunkBytes = maxChunkBytes;
        }

        internal string ClientId { get; }
        internal string StreamId { get; }
        internal int SampleRate { get; }
        internal int MaxSeconds { get; }
        internal int MaxChunkBytes { get; }
        internal long BytesReceived;
        internal TranscriptionService Service { get; } = new();
        internal TranscriptBuffer Buffer { get; } = new();
        internal object BufferGate { get; } = new();
        internal Dictionary<int, byte[]> ChunkDigests { get; } = new();
        internal int NextSeq;
        internal bool Active = true;
        internal string LastDraft = "";
    }
}

internal sealed class VoiceAudioException : Exception
{
    internal VoiceAudioException(string code, string message) : base(message) => Code = code;
    internal string Code { get; }
}
