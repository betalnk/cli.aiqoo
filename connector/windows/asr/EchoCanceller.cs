namespace LiveTranscribeRu;

/// <summary>
/// Подавление акустического эха (AEC) для сценария без наушников: микрофон ловит и ваш
/// голос, и звук из колонок. Зная, что играет в колонках (системный loopback — опорный
/// сигнal), адаптивный фильтр NLMS оценивает «путь динамик→воздух→микрофон» и вычитает
/// эхо из микрофона, оставляя только ближнюю речь (вас).
///
/// Время-доменный NLMS с детектором двойного разговора (DTD): пока говорит только
/// собеседник (из колонок) — фильтр учится и вычитает эхо; когда говорите вы — адаптация
/// замораживается, чтобы фильтр не «съел» ваш голос.
/// </summary>
public sealed class EchoCanceller
{
    readonly int _l;            // длина фильтра (тапы), покрывает задержку + хвост эха
    readonly float[] _w;        // веса фильтра
    readonly float[] _x;        // рабочий буфер опорного сигнала: префикс _l + блок
    float _xpow;                // скользящая энергия окна опорного сигнала
    float _micPow = 1e-6f;      // EMA энергии микрофона (для DTD)
    float _echoPow = 1e-6f;     // EMA энергии оценённого эха (для DTD)

    const float Mu = 0.3f;      // шаг адаптации
    const float Eps = 1e-3f;

    public EchoCanceller(int filterLen, int maxBlock)
    {
        _l = filterLen;
        _w = new float[_l];
        _x = new float[_l + maxBlock];
    }

    /// <summary>
    /// Очищает mic[0..n) от эха, используя ref[0..n) (системный звук того же тика) как опорный.
    /// Результат записывается обратно в mic.
    /// </summary>
    public void Process(float[] mic, int micN, float[] reff, int refN)
    {
        int n = Math.Min(micN, refN);
        if (n <= 0) return;

        Array.Copy(reff, 0, _x, _l, n);   // новый блок опорного — в конец (префикс = прошлые _l отсчётов)

        for (int j = 0; j < n; j++)
        {
            int end = _l + j;             // индекс текущего опорного отсчёта
            int oldest = j;               // = end - _l, покидает окно

            // y = Σ w[k]·x[end-k] — оценка эха
            float y = 0;
            for (int k = 0; k < _l; k++) y += _w[k] * _x[end - k];

            float d = mic[j];
            float e = d - y;              // остаток = ваша речь без эха

            // скользящая энергия опорного окна
            _xpow += _x[end] * _x[end] - _x[oldest] * _x[oldest];
            if (_xpow < 0) _xpow = 0;

            _micPow = 0.995f * _micPow + 0.005f * d * d;
            _echoPow = 0.995f * _echoPow + 0.005f * y * y;

            // DTD: двойной разговор, если микрофон заметно громче оценённого эха →
            // значит есть ближняя речь → замораживаем адаптацию (иначе фильтр разойдётся)
            bool doubleTalk = _micPow > 2.0f * _echoPow + 1e-5f;
            bool refActive = _xpow > 1e-4f;

            if (refActive && !doubleTalk)
            {
                float g = Mu * e / (_xpow + Eps);
                for (int k = 0; k < _l; k++) _w[k] += g * _x[end - k];
            }

            mic[j] = e;
        }

        Array.Copy(_x, n, _x, 0, _l);   // последние _l отсчётов опорного → в префикс
    }
}
