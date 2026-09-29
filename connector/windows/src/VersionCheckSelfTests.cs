using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexVoice;

internal static class VersionCheckSelfTests
{
    internal static async Task RunAsync()
    {
        var current = new Version(0, 1, 0);
        var release = "https://github.com/betalnk/cli.aiqoo/releases/tag/v0.2.0";
        var scripted = new ScriptedHandler(
            request =>
            {
                Check(request.RequestUri == VersionCheckService.ManifestUri);
                Check(request.Headers.IfNoneMatch.Count == 0);
                return Manifest("0.2.0", release, "first");
            },
            request =>
            {
                Check(request.Headers.IfNoneMatch.Any(tag => tag.Tag == "\"first\""));
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            });
        using (var http = new HttpClient(scripted))
        using (var service = new VersionCheckService(http, current))
        {
            var found = await service.CheckAsync();
            Check(found.State == VersionCheckState.Available &&
                found.RemoteVersion == new Version(0, 2, 0) &&
                found.ReleaseUrl?.AbsoluteUri == release);
            var unchanged = await service.CheckAsync();
            Check(unchanged == found && scripted.Requests == 2);
        }

        var equal = await CheckOnceAsync(current, _ => Manifest("0.1.0", null));
        Check(equal.State == VersionCheckState.Current);
        var absent = await CheckOnceAsync(current, _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Check(absent.State == VersionCheckState.NotPublished);
        var pending = await CheckOnceAsync(current, _ => Manifest("0.2.0", null));
        Check(pending.State == VersionCheckState.NotPublished);
        var unsafeLink = await CheckOnceAsync(current, _ => Manifest("0.2.0", "https://example.com/update.exe"));
        Check(unsafeLink.State == VersionCheckState.NotPublished);
        var malformed = await CheckOnceAsync(current, _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{bad") });
        Check(malformed.State == VersionCheckState.Unavailable);
        var offline = await CheckOnceAsync(current, _ => throw new HttpRequestException("offline"));
        Check(offline.State == VersionCheckState.Unavailable);
        Check(!VersionCheckService.TryReadManifest("{\"schema\":\"bad\",\"version\":\"0.2.0\"}",
            current, out _));

        var now = DateTimeOffset.Parse("2026-09-28T00:00:00Z");
        var limited = new ScriptedHandler(
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
                return response;
            },
            _ => Manifest("0.1.0", null));
        using (var http = new HttpClient(limited))
        using (var service = new VersionCheckService(http, current, () => now))
        {
            Check((await service.CheckAsync()).State == VersionCheckState.Unavailable);
            Check((await service.CheckAsync()).State == VersionCheckState.Unavailable && limited.Requests == 1);
            now = now.AddMinutes(3);
            Check((await service.CheckAsync()).State == VersionCheckState.Current && limited.Requests == 2);
        }
    }

    private static async Task<VersionCheckResult> CheckOnceAsync(
        Version current, Func<HttpRequestMessage, HttpResponseMessage> response)
    {
        using var http = new HttpClient(new ScriptedHandler(response));
        using var service = new VersionCheckService(http, current);
        return await service.CheckAsync();
    }

    private static HttpResponseMessage Manifest(string version, string? releaseUrl, string? etag = null)
    {
        var result = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { schema = 1, version, releaseUrl }))
        };
        if (etag is not null) result.Headers.ETag = new EntityTagHeaderValue("\"" + etag + "\"");
        return result;
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Version-check self-test failed.");
    }

    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] replies)
        : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _replies = new(replies);
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(_replies.Dequeue()(request));
        }
    }
}
