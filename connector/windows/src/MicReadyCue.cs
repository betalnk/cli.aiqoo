using NAudio.Wave;

namespace CodexVoice;

/// <summary>A short, quiet, locally generated cue for a ready microphone.</summary>
internal sealed class MicReadyCue : IDisposable
{
    private const int SampleRate = 44_100;
    private static readonly WaveFormat Format = new(SampleRate, 16, 1);
    private static readonly byte[] Pcm = GeneratePcm();

    private readonly object _gate = new();
    private Playback? _current;
    private long _generation;
    private bool _disposed;

    /// <summary>Starts the cue in the background. Audio-device errors never block capture.</summary>
    internal void Play()
    {
        long generation;
        lock (_gate)
        {
            if (_disposed) return;
            generation = ++_generation;
        }
        _ = Task.Run(() => Start(generation));
    }

    internal void Stop()
    {
        Playback? previous;
        lock (_gate)
        {
            if (_disposed) return;
            ++_generation;
            previous = _current;
            _current = null;
        }
        previous?.Dispose();
    }

    public void Dispose()
    {
        Playback? previous;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ++_generation;
            previous = _current;
            _current = null;
        }
        previous?.Dispose();
    }

    private void Start(long generation)
    {
        Playback? playback = null;
        try
        {
            playback = new Playback(Pcm, Format, OnPlaybackStopped);
            Playback? previous;
            lock (_gate)
            {
                if (_disposed || generation != _generation)
                {
                    playback.Dispose();
                    return;
                }
                previous = _current;
                _current = playback;
            }

            previous?.Dispose();
            playback.Play();
        }
        catch
        {
            // A missing or busy output device should never prevent recording.
            playback?.Dispose();
        }
    }

    private void OnPlaybackStopped(Playback playback)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, playback)) _current = null;
        }
        // Dispose outside WaveOutEvent's playback callback.
        _ = Task.Run(playback.Dispose);
    }

    private static byte[] GeneratePcm()
    {
        const double firstSeconds = 0.052;
        const double gapSeconds = 0.006;
        const double secondSeconds = 0.062;
        var firstSamples = (int)(SampleRate * firstSeconds);
        var gapSamples = (int)(SampleRate * gapSeconds);
        var secondSamples = (int)(SampleRate * secondSeconds);
        var pcm = new byte[(firstSamples + gapSamples + secondSamples) * sizeof(short)];

        WriteNote(pcm, 0, firstSamples, 523.25, 0.075);
        WriteNote(pcm, firstSamples + gapSamples, secondSamples, 659.25, 0.065);
        return pcm;
    }

    private static void WriteNote(byte[] pcm, int sampleOffset, int sampleCount,
        double frequency, double amplitude)
    {
        for (var index = 0; index < sampleCount; index++)
        {
            var seconds = index / (double)SampleRate;
            var remaining = (sampleCount - index) / (double)SampleRate;
            var attack = Math.Min(1, seconds / 0.004);
            var release = Math.Min(1, remaining / 0.016);
            var phase = 2 * Math.PI * frequency * seconds;
            var sample = amplitude * attack * release *
                (Math.Sin(phase) + 0.12 * Math.Sin(2 * phase));
            var value = (short)Math.Round(Math.Clamp(sample, -1, 1) * short.MaxValue);
            var byteOffset = (sampleOffset + index) * sizeof(short);
            pcm[byteOffset] = (byte)value;
            pcm[byteOffset + 1] = (byte)(value >> 8);
        }
    }

    private sealed class Playback : IDisposable
    {
        private readonly MemoryStream _buffer;
        private readonly RawSourceWaveStream _stream;
        private readonly WaveOutEvent _output;
        private readonly Action<Playback> _onStopped;
        private int _disposed;

        internal Playback(byte[] pcm, WaveFormat format, Action<Playback> onStopped)
        {
            _onStopped = onStopped;
            _buffer = new MemoryStream(pcm, writable: false);
            _stream = new RawSourceWaveStream(_buffer, format);
            _output = new WaveOutEvent { DesiredLatency = 60, NumberOfBuffers = 2 };
            try
            {
                _output.PlaybackStopped += HandlePlaybackStopped;
                _output.Init(_stream);
            }
            catch
            {
                _output.PlaybackStopped -= HandlePlaybackStopped;
                _output.Dispose();
                _stream.Dispose();
                _buffer.Dispose();
                throw;
            }
        }

        internal void Play() => _output.Play();

        private void HandlePlaybackStopped(object? sender, StoppedEventArgs args) => _onStopped(this);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _output.PlaybackStopped -= HandlePlaybackStopped;
            try { _output.Stop(); }
            catch { /* Output may already have stopped or been removed. */ }
            _output.Dispose();
            _stream.Dispose();
            _buffer.Dispose();
        }
    }
}
