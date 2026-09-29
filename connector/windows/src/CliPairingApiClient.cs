using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CodexVoice;

/// <summary>Starts a one-time CLI pairing; only the browser and this PC know its QR secret.</summary>
internal sealed class CliPairingApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly VoicePairingStore _store;
    private readonly VoicePairingManager _manager;
    private readonly Uri _origin;

    internal CliPairingApiClient(VoicePairingStore store, VoicePairingManager manager,
        HttpClient? http = null, Uri? origin = null)
    {
        _store = store;
        _manager = manager;
        _origin = origin ?? new Uri("https://cli.aiqoo.ru/");
        if (_origin.Scheme != Uri.UriSchemeHttps || _origin.AbsolutePath != "/"
            || _origin.Query.Length > 0 || _origin.Fragment.Length > 0)
            throw new ArgumentException("CLI origin must be an HTTPS origin.", nameof(origin));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.MaxResponseContentBufferSize = 16 * 1024;
    }

    internal async Task<VoicePairingOffer> StartAsync(CancellationToken token = default)
    {
        using var key = _store.LoadOrCreateDeviceKey();
        var publicKey = VoicePairingCrypto.EncodeBase64Url(VoicePairingCrypto.ExportPublicKey(key));
        var registration = _store.LoadDeviceRegistration();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(_origin, "api/v1/pairings/start"));
        if (registration is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Device", registration.DeviceToken);
        else
            request.Content = JsonContent.Create(new
            {
                deviceName = DeviceName(),
                devicePublicKey = publicKey
            });
        request.Content ??= JsonContent.Create(new { });

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
        if (response.StatusCode == HttpStatusCode.Unauthorized && registration is not null)
        {
            // The user explicitly requested a new QR code. Discard only the revoked
            // registration and retry once with the same locally protected device key.
            if (!_store.ForgetRevokedRegistration(registration))
                throw new InvalidOperationException("Регистрация ПК изменилась. Повторите подключение.");
            return await StartAsync(token);
        }
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Сервер CLI не выдал код подключения (HTTP {(int)response.StatusCode}).");
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(stream,
            new JsonDocumentOptions { MaxDepth = 4 }, token);
        var root = document.RootElement;
        var deviceId = root.GetProperty("deviceId").GetString()!;
        var pairId = root.GetProperty("pairId").GetString()!;
        var code = root.GetProperty("code").GetString()!;
        var expiresAt = root.GetProperty("expiresAt").GetInt64();
        VoicePairingCrypto.ValidateHexId(deviceId, nameof(deviceId));
        VoicePairingCrypto.ValidateHexId(pairId, nameof(pairId));
        _ = VoicePairingCrypto.NormalizeCode(code);
        if (expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            || expiresAt > DateTimeOffset.UtcNow.AddMinutes(6).ToUnixTimeSeconds())
            throw new InvalidDataException("Срок кода подключения от сервера неверен.");

        if (registration is null)
        {
            var deviceToken = root.GetProperty("deviceToken").GetString()!;
            _store.SaveDeviceRegistration(deviceId, deviceToken);
        }
        else
        {
            if (deviceId != registration.DeviceId
                || root.GetProperty("deviceToken").ValueKind != JsonValueKind.Null)
                throw new InvalidDataException("Сервер вернул другую регистрацию ПК.");
        }
        return _manager.Begin(pairId, code, expiresAt);
    }

    private static string DeviceName()
    {
        var value = new string(Environment.MachineName
            .Where(character => !char.IsControl(character)).Take(80).ToArray()).Trim();
        return value.Length > 0 ? value : "Компьютер";
    }

    public void Dispose() => _http.Dispose();
}
