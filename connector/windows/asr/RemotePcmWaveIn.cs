using NAudio.Wave;

namespace LiveTranscribeRu;

/// <summary>In-memory IWaveIn source for paired-phone PCM; never opens a Windows audio device.</summary>
internal sealed class RemotePcmWaveIn : IWaveIn
{
    private bool _running;
    private bool _disposed;

    internal RemotePcmWaveIn(int sampleRate)
    {
        if (sampleRate is < 8000 or > 96000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        WaveFormat = new WaveFormat(sampleRate, 16, 1);
    }

    public WaveFormat WaveFormat { get; set; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RemotePcmWaveIn));
        _running = true;
    }

    internal void Feed(ReadOnlySpan<byte> pcm)
    {
        if (!_running || _disposed) throw new InvalidOperationException("Phone PCM source is stopped.");
        if (pcm.Length == 0 || (pcm.Length & 1) != 0)
            throw new ArgumentException("Expected complete 16-bit mono samples.", nameof(pcm));
        var bytes = pcm.ToArray();
        DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));
    }

    public void StopRecording()
    {
        if (!_running) return;
        _running = false;
        RecordingStopped?.Invoke(this, new StoppedEventArgs());
    }

    public void Dispose()
    {
        StopRecording();
        _disposed = true;
    }
}
