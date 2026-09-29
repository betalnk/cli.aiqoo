using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LiveTranscribeRu;

/// <summary>
/// Silero VAD (v5) — нейросетевой детектор речи. На каждый кадр в 512 сэмплов (16 кГц)
/// возвращает вероятность того, что это речь. Отличает голос от музыки/шума/тишины
/// надёжнее любого порога по громкости. Состояние LSTM тянется между кадрами потока.
/// </summary>
public sealed class SileroVad : IDisposable
{
    public const int FrameSamples = 512;     // размер кадра, который подаёт конвейер (16 кГц)
    const int ContextSamples = 64;           // v5 требует 64 сэмпла контекста перед кадром

    readonly InferenceSession _session;
    float[] _state = new float[2 * 1 * 128];
    float[] _context = new float[ContextSamples];
    readonly long[] _sr = { 16000 };

    public SileroVad(string modelPath) => _session = new InferenceSession(modelPath);

    /// <summary>
    /// Вероятность речи [0..1] для кадра ровно из FrameSamples сэмплов.
    /// На вход модели уходит 64 (контекст) + 512 = 576 сэмплов — иначе Silero выдаёт мусор.
    /// </summary>
    public float Probability(float[] frame)
    {
        var buf = new float[ContextSamples + frame.Length];
        Array.Copy(_context, 0, buf, 0, ContextSamples);
        Array.Copy(frame, 0, buf, ContextSamples, frame.Length);
        Array.Copy(frame, frame.Length - ContextSamples, _context, 0, ContextSamples); // контекст для след. кадра

        var input = new DenseTensor<float>(buf, new[] { 1, buf.Length });
        var state = new DenseTensor<float>(_state, new[] { 2, 1, 128 });
        var sr = new DenseTensor<long>(_sr, new[] { 1 });

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input", input),
            NamedOnnxValue.CreateFromTensor("state", state),
            NamedOnnxValue.CreateFromTensor("sr", sr),
        };

        using var results = _session.Run(inputs);
        float prob = 0;
        foreach (var r in results)
        {
            if (r.Name == "output") prob = r.AsTensor<float>().ToArray()[0];
            else if (r.Name == "stateN") _state = r.AsTensor<float>().ToArray();
        }
        return prob;
    }

    /// <summary>Сбросить состояние и контекст перед новым потоком.</summary>
    public void Reset()
    {
        Array.Clear(_state);
        Array.Clear(_context);
    }

    public void Dispose() => _session.Dispose();
}
