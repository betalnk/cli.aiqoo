using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LiveTranscribeRu;

/// <summary>
/// Онлайн-диаризация собеседников: по аудио сегмента считает голосовой отпечаток
/// (эмбеддинг нейросетью) и относит к одному из уже виденных говорящих по косинусной
/// близости; если близкого нет — заводит нового «Участник N». Личность сохраняется на
/// всю сессию (даже если участник заговорит снова через 15 минут).
///
/// Включается только при наличии файла модели <see cref="ModelFile"/> рядом с приложением.
/// Модель — стандартный ONNX speaker-embedding (вход: лог-мел fbank [1, T, 80],
/// выход: вектор эмбеддинга). Имена входа/выхода берутся из метаданных модели.
/// </summary>
public sealed class SpeakerDiarizer : IDisposable
{
    public const string ModelFile = "speaker-embedding.onnx";

    const int MelBins = 80;
    const int FrameLen = 400;     // 25 мс при 16 кГц
    const int FrameHop = 160;     // 10 мс
    const int FftSize = 512;
    // консервативно: склоняемся объединять одного говорящего, а не плодить новых
    const float MatchSim = 0.45f;      // >= → уверенно тот же говорящий
    const float NewSim = 0.25f;        // < → уверенно другой (только тогда заводим нового)
    const int MinSamples = 8000;       // < 0.5 c — слишком коротко, оставляем прежнего
    const int NewSpeakerMinSamples = 16000;  // нового заводим только с фразы >= 1 c

    const float Preemph = 0.97f;       // преэмфазис как в kaldi/wespeaker
    const int EmbedMaxSamples = 16000 * 4;   // для отпечатка хватает ~4 c — ограничиваем ради скорости

    readonly InferenceSession _session;
    readonly string _inputName, _outputName;
    readonly int _rate;
    readonly Action<string> _log;
    readonly float[][] _melFilters;
    readonly float[] _povey;           // окно Повея (kaldi)

    // центроиды говорящих: сумма эмбеддингов и количество (центроид = normalize(sum))
    readonly List<float[]> _sum = new();
    readonly List<int> _count = new();
    string _lastKey = "remote1";

    SpeakerDiarizer(string modelPath, int rate, Action<string> log)
    {
        _session = new InferenceSession(modelPath);
        _rate = rate;
        _log = log;
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
        _melFilters = BuildMelFilters(rate);

        _povey = new float[FrameLen];
        for (int i = 0; i < FrameLen; i++)
            _povey[i] = MathF.Pow(0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (FrameLen - 1)), 0.85f);
    }

    public static SpeakerDiarizer? TryCreate(int rate, Action<string> log)
    {
        var path = Path.Combine(AppContext.BaseDirectory, ModelFile);
        if (!File.Exists(path)) return null;
        try { return new SpeakerDiarizer(path, rate, log); }
        catch (Exception ex) { log($"диаризация: не удалось загрузить модель: {ex.Message}"); return null; }
    }

    /// <summary>Ключ говорящего («remote1», «remote2», …) для аудио сегмента.</summary>
    public string Identify(IReadOnlyList<float> samples)
    {
        if (samples.Count < MinSamples) return _lastKey;   // коротко — оставляем последнего
        try
        {
            // для отпечатка берём не более последних ~4 c — быстрее и без потери качества
            if (samples.Count > EmbedMaxSamples)
            {
                var slice = new float[EmbedMaxSamples];
                for (int i = 0; i < EmbedMaxSamples; i++) slice[i] = samples[samples.Count - EmbedMaxSamples + i];
                samples = slice;
            }
            var emb = Normalize(Embed(samples));
            int best = -1; float bestSim = -1f;
            for (int i = 0; i < _sum.Count; i++)
            {
                var sim = Cosine(emb, Normalize((float[])_sum[i].Clone()));
                if (sim > bestSim) { bestSim = sim; best = i; }
            }

            int idx;
            if (best < 0)
            {
                idx = AddSpeaker(emb);   // первый говорящий
            }
            else if (bestSim >= MatchSim)
            {
                idx = best;              // уверенно тот же — объединяем
                for (int d = 0; d < emb.Length; d++) _sum[idx][d] += emb[d];
                _count[idx]++;
            }
            else if (bestSim < NewSim && samples.Count >= NewSpeakerMinSamples)
            {
                idx = AddSpeaker(emb);   // уверенно другой и фраза достаточно длинная
                _log($"диаризация: новый говорящий remote{idx + 1} (близость {bestSim:F2})");
            }
            else
            {
                idx = best;              // неуверенно — НЕ плодим, относим к ближайшему
            }
            _log($"диаризация: → remote{idx + 1} (близость {bestSim:F2}, говорящих {_sum.Count})");
            _lastKey = $"remote{idx + 1}";
            return _lastKey;
        }
        catch (Exception ex) { _log($"диаризация: ошибка эмбеддинга: {ex.Message}"); return _lastKey; }
    }

    int AddSpeaker(float[] emb)
    {
        int idx = _sum.Count;
        _sum.Add((float[])emb.Clone());
        _count.Add(1);
        return idx;
    }

    // --- Эмбеддинг ---

    float[] Embed(IReadOnlyList<float> samples)
    {
        var feats = Fbank(samples);                 // [T, 80]
        int t = feats.Length;
        var flat = new float[t * MelBins];
        for (int i = 0; i < t; i++) Array.Copy(feats[i], 0, flat, i * MelBins, MelBins);

        var input = new DenseTensor<float>(flat, new[] { 1, t, MelBins });
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
        return results.First(r => r.Name == _outputName).AsTensor<float>().ToArray();
    }

    float[][] Fbank(IReadOnlyList<float> x)
    {
        int frames = x.Count < FrameLen ? 1 : 1 + (x.Count - FrameLen) / FrameHop;
        var outp = new float[frames][];
        var re = new float[FftSize];
        var im = new float[FftSize];

        var frame = new float[FrameLen];
        for (int f = 0; f < frames; f++)
        {
            Array.Clear(re); Array.Clear(im);
            int start = f * FrameHop;
            for (int i = 0; i < FrameLen; i++)
            {
                int si = start + i;
                frame[i] = si < x.Count ? x[si] : 0f;
            }
            // 1) снятие постоянной составляющей (DC offset)
            float dc = 0; for (int i = 0; i < FrameLen; i++) dc += frame[i];
            dc /= FrameLen;
            for (int i = 0; i < FrameLen; i++) frame[i] -= dc;
            // 2) преэмфазис 0.97 (с конца, чтобы использовать исходный предыдущий отсчёт)
            for (int i = FrameLen - 1; i > 0; i--) frame[i] -= Preemph * frame[i - 1];
            frame[0] -= Preemph * frame[0];
            // 3) окно Повея
            for (int i = 0; i < FrameLen; i++) re[i] = frame[i] * _povey[i];

            Fft(re, im);

            var mel = new float[MelBins];
            for (int m = 0; m < MelBins; m++)
            {
                float sum = 0;
                var filt = _melFilters[m];
                for (int k = 0; k < filt.Length; k++)
                {
                    if (filt[k] == 0) continue;
                    float power = re[k] * re[k] + im[k] * im[k];
                    sum += filt[k] * power;
                }
                mel[m] = MathF.Log(MathF.Max(sum, 1e-10f));
            }
            outp[f] = mel;
        }

        // нормализация среднего по каждому коэффициенту (CMN) — для устойчивости отпечатка
        var mean = new float[MelBins];
        foreach (var fr in outp) for (int m = 0; m < MelBins; m++) mean[m] += fr[m];
        for (int m = 0; m < MelBins; m++) mean[m] /= outp.Length;
        foreach (var fr in outp) for (int m = 0; m < MelBins; m++) fr[m] -= mean[m];
        return outp;
    }

    float[][] BuildMelFilters(int rate)
    {
        int bins = FftSize / 2 + 1;
        float fMin = 20f, fMax = rate / 2f;
        float MelOf(float hz) => 2595f * MathF.Log10(1f + hz / 700f);
        float HzOf(float mel) => 700f * (MathF.Pow(10f, mel / 2595f) - 1f);

        float melMin = MelOf(fMin), melMax = MelOf(fMax);
        var points = new float[MelBins + 2];
        for (int i = 0; i < points.Length; i++)
        {
            float mel = melMin + (melMax - melMin) * i / (MelBins + 1);
            points[i] = HzOf(mel) * FftSize / rate;   // в единицах FFT-бинов
        }

        var filters = new float[MelBins][];
        for (int m = 0; m < MelBins; m++)
        {
            filters[m] = new float[bins];
            float left = points[m], center = points[m + 1], right = points[m + 2];
            for (int k = 0; k < bins; k++)
            {
                float v = 0;
                if (k >= left && k <= center && center > left) v = (k - left) / (center - left);
                else if (k > center && k <= right && right > center) v = (right - k) / (right - center);
                filters[m][k] = v;
            }
        }
        return filters;
    }

    // Итеративный БПФ (radix-2, размер FftSize). re/im — на входе сигнал, на выходе спектр.
    static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            float ang = -2f * MathF.PI / len;
            float wr = MathF.Cos(ang), wi = MathF.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cr = 1f, ci = 0f;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    float tr = re[b] * cr - im[b] * ci;
                    float ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    float ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
    }

    static float[] Normalize(float[] v)
    {
        double s = 0; foreach (var x in v) s += x * x;
        float n = (float)Math.Sqrt(s) + 1e-9f;
        var o = new float[v.Length];
        for (int i = 0; i < v.Length; i++) o[i] = v[i] / n;
        return o;
    }

    static float Cosine(float[] a, float[] b)
    {
        float d = 0; int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) d += a[i] * b[i];
        return d;
    }

    public void Dispose() => _session.Dispose();
}
