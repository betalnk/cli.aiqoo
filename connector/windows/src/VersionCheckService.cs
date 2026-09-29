using System.Net;
using System.Text.Json;

namespace CodexVoice;

internal enum VersionCheckState
{
    Current,
    Available,
    NotPublished,
    Unavailable
}

internal sealed record VersionCheckResult(
    VersionCheckState State, Version? RemoteVersion = null, Uri? ReleaseUrl = null);

/// <summary>Reads the Windows connector's release pointer from public main without sending user data.</summary>
internal sealed class VersionCheckService : IDisposable
{
    internal static readonly Uri ManifestUri = new(
        "https://raw.githubusercontent.com/betalnk/cli.aiqoo/main/connector/windows/version.json");

    private static readonly Uri ReleasesUri = new("https://github.com/betalnk/cli.aiqoo/releases");
    private readonly HttpClient _http;
    private readonly Version _currentVersion;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly bool _ownsHttp;
    private VersionCheckResult? _cached;
    private string? _etag;
    private DateTimeOffset _retryAfterUtc;

    internal static Uri ReleasesPage => ReleasesUri;

    internal static Version CurrentVersion
    {
        get
        {
            var version = typeof(VersionCheckService).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }

    internal VersionCheckService()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(8) }, CurrentVersion, null, ownsHttp: true)
    {
    }

    internal VersionCheckService(HttpClient http, Version currentVersion, Func<DateTimeOffset>? utcNow = null)
        : this(http, currentVersion, utcNow, ownsHttp: false)
    {
    }

    private VersionCheckService(HttpClient http, Version currentVersion, Func<DateTimeOffset>? utcNow, bool ownsHttp)
    {
        _http = http;
        _currentVersion = currentVersion;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _ownsHttp = ownsHttp;
    }

    internal async Task<VersionCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_utcNow() < _retryAfterUtc)
            return new VersionCheckResult(VersionCheckState.Unavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUri);
            request.Headers.UserAgent.ParseAdd("CodexVoice/" + _currentVersion.ToString(3));
            if (_etag is not null)
                request.Headers.TryAddWithoutValidation("If-None-Match", _etag);

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
                return _cached ?? new VersionCheckResult(VersionCheckState.Unavailable);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _etag = null;
                return _cached = new VersionCheckResult(VersionCheckState.NotPublished);
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter?.Delta
                    ?? response.Headers.RetryAfter?.Date - _utcNow()
                    ?? TimeSpan.FromMinutes(20);
                _retryAfterUtc = _utcNow() + TimeSpan.FromMinutes(
                    Math.Clamp(retry.TotalMinutes, 1, 60));
                return new VersionCheckResult(VersionCheckState.Unavailable);
            }
            if (!response.IsSuccessStatusCode)
                return new VersionCheckResult(VersionCheckState.Unavailable);
            if (response.Content.Headers.ContentLength is > 8192)
                return new VersionCheckResult(VersionCheckState.Unavailable);

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (json.Length > 8192 || !TryReadManifest(json, _currentVersion, out var result))
                return new VersionCheckResult(VersionCheckState.Unavailable);

            _etag = response.Headers.ETag?.ToString();
            return _cached = result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new VersionCheckResult(VersionCheckState.Unavailable);
        }
    }

    internal static bool TryReadManifest(string json, Version currentVersion, out VersionCheckResult result)
    {
        result = new VersionCheckResult(VersionCheckState.Unavailable);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaNumber) || schemaNumber != 1 ||
                !root.TryGetProperty("version", out var versionProperty) ||
                versionProperty.ValueKind != JsonValueKind.String)
                return false;

            var versionText = versionProperty.GetString();
            if (versionText is null || versionText.Length > 32 || versionText.Split('.').Length != 3 ||
                !Version.TryParse(versionText, out var remoteVersion))
                return false;

            if (remoteVersion <= currentVersion)
            {
                result = new VersionCheckResult(VersionCheckState.Current, remoteVersion);
                return true;
            }

            if (root.TryGetProperty("releaseUrl", out var urlProperty) &&
                urlProperty.ValueKind == JsonValueKind.String &&
                TryReleaseUrl(urlProperty.GetString(), versionText, out var releaseUrl))
            {
                result = new VersionCheckResult(VersionCheckState.Available, remoteVersion, releaseUrl);
                return true;
            }

            result = new VersionCheckResult(VersionCheckState.NotPublished, remoteVersion);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReleaseUrl(string? value, string version, out Uri? releaseUrl)
    {
        releaseUrl = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            !parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !parsed.IsDefaultPort || parsed.UserInfo.Length != 0 ||
            parsed.AbsolutePath != "/betalnk/cli.aiqoo/releases/tag/v" + version ||
            parsed.Query.Length != 0 || parsed.Fragment.Length != 0)
            return false;

        releaseUrl = parsed;
        return true;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
