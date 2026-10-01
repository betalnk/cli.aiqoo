using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexVoice;

/// <summary>Receives CLI account pairing claims and verifies their QR proof on this PC.</summary>
internal sealed class CliDeviceConnection : IDisposable
{
    private const int MaxControlFrameBytes = 4096;
    private const int MaxCommandChunkFrameBytes = 16384;
    private readonly VoicePairingStore _store;
    private readonly VoicePairingManager _manager;
    private readonly Action<string> _status;
    private readonly Func<bool> _localInputIdle;
    private readonly Func<VoicePairingApprovalRequest, CancellationToken, Task<bool>> _approve;
    private readonly Uri _socketUri;
    private readonly string _origin;
    private readonly SemaphoreSlim _registrationWake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;

    internal CliDeviceConnection(VoicePairingStore store, VoicePairingManager manager,
        Action<string> status, Func<bool>? localInputIdle = null, Uri? socketUri = null,
        Func<VoicePairingApprovalRequest, CancellationToken, Task<bool>>? approve = null)
    {
        _store = store;
        _manager = manager;
        _status = status;
        _localInputIdle = localInputIdle ?? (() => false);
        _approve = approve ?? ((_, _) => Task.FromResult(false));
        _socketUri = socketUri ?? new Uri("wss://cli.aiqoo.ru/api/v1/ws/device");
        if (_socketUri.Scheme != "wss" || _socketUri.Query.Length > 0
            || _socketUri.Fragment.Length > 0 || _socketUri.UserInfo.Length > 0)
            throw new ArgumentException("CLI device WebSocket must use WSS without URL credentials.", nameof(socketUri));
        _origin = "https://" + _socketUri.Authority;
    }

    internal void Start()
    {
        if (_run is null || _run.IsCompleted) _run = RunAsync(_stop.Token);
        RegistrationAvailable();
    }

    internal void RegistrationAvailable()
    {
        if (_registrationWake.CurrentCount == 0) _registrationWake.Release();
    }

    private async Task RunAsync(CancellationToken token)
    {
        var retrySeconds = 2;
        while (!token.IsCancellationRequested)
        {
            VoiceDeviceRegistration? registration;
            try { registration = _store.LoadDeviceRegistration(); }
            catch
            {
                _status("Не удалось прочитать защищённую привязку ПК.");
                return;
            }
            if (registration is null)
            {
                try { await _registrationWake.WaitAsync(token); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.CollectHttpResponseDetails = true;
                socket.Options.SetRequestHeader("Authorization", "Device " + registration.DeviceToken);
                socket.Options.SetRequestHeader("Origin", _origin);
                try { await socket.ConnectAsync(_socketUri, token); }
                catch (WebSocketException) when (socket.HttpStatusCode is
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    _status("Сервер CLI отклонил ключ этого ПК. Подключите его заново в аккаунте.");
                    await WaitForNewRegistrationAsync(registration, token);
                    continue;
                }
                retrySeconds = 2;
                using var connected = CancellationTokenSource.CreateLinkedTokenSource(token);
                await using var history = new CliHistoryWatchManager(
                    _store, registration.DeviceId, socket, connected.Token);
                await using var commands = new CliCommandManager(_store,
                    registration.DeviceId, history.SendJsonAsync, _localInputIdle,
                    connected.Token);
                await using var approvals = new CliPairingApprovalCoordinator(_manager, _approve,
                    (claim, accepted, cancellation) => SendDecisionAsync(history, claim.EventId,
                        claim.PairId, claim.ClientId, accepted, cancellation), connected.Token);
                foreach (var decision in _manager.PendingDecisions())
                    await SendDecisionAsync(history, decision.EventId, decision.PairId,
                        decision.ClientId, accepted: true, connected.Token);
                var heartbeat = new DeviceHeartbeat();
                var heartbeatTask = RunHeartbeatAsync(history, heartbeat, connected);
                try { await ReceiveLoopAsync(socket, history, commands, approvals, heartbeat, connected.Token); }
                finally
                {
                    connected.Cancel();
                    try { await heartbeatTask; }
                    catch (OperationCanceledException) { }
                }
                if (socket.CloseStatus == WebSocketCloseStatus.PolicyViolation)
                {
                    _status("Сервер CLI отклонил привязку этого ПК. Проверьте доступ в аккаунте.");
                    return;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception)
            {
                // Keep the tray process available offline; reconnect without exposing credentials.
            }
            try { await Task.Delay(TimeSpan.FromSeconds(retrySeconds), token); }
            catch (OperationCanceledException) { return; }
            retrySeconds = Math.Min(retrySeconds * 2, 30);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket,
        CliHistoryWatchManager history, CliCommandManager commands,
        CliPairingApprovalCoordinator approvals, DeviceHeartbeat heartbeat, CancellationToken token)
    {
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            using var stream = new MemoryStream();
            ValueWebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer.AsMemory(), token);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", token);
                    return;
                }
                if (received.MessageType != WebSocketMessageType.Text
                    || stream.Length + received.Count > MaxCommandChunkFrameBytes)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "invalid frame", token);
                    return;
                }
                stream.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);

            JsonDocument document;
            try { document = JsonDocument.Parse(stream.ToArray(), new JsonDocumentOptions { MaxDepth = 4 }); }
            catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var kind)
                    || kind.ValueKind != JsonValueKind.String) continue;
                var commandChunk = kind.GetString() == "command_chunk";
                if (stream.Length > (commandChunk ? MaxCommandChunkFrameBytes : MaxControlFrameBytes))
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "invalid frame", token);
                    return;
                }
                if (kind.GetString() == "paired")
                    QueueClaim(approvals, root);
                else if (kind.GetString() == "pair_accepted")
                {
                    if (TryReadIds(root, out var eventId, out var pairId, out var clientId))
                    {
                        _manager.ConfirmDecision(eventId, pairId, clientId);
                        _status("Компьютер подключён к аккаунту CLI.");
                    }
                }
                else if (kind.GetString() == "pair_rejected")
                    _status("Запрос на подключение отклонён этим ПК.");
                else if (kind.GetString() == "decision_rejected")
                {
                    if (TryReadIds(root, out var eventId, out var pairId, out var clientId))
                    {
                        _manager.RejectDecision(eventId, pairId, clientId);
                        _status("Код подключения истёк или уже использован. Создайте новый код.");
                    }
                }
                else if (kind.GetString() == "history_request")
                    history.HandleRequest(root);
                else if (kind.GetString() == "history_cancel")
                    history.HandleCancel(root);
                else if (kind.GetString() == "command_start")
                    await commands.HandleStartAsync(root);
                else if (kind.GetString() == "command_chunk")
                    await commands.HandleChunkAsync(root);
                else if (kind.GetString() == "command_end")
                    await commands.HandleEndAsync(root);
                else if (kind.GetString() == "command_cancel")
                    commands.HandleCancel(root);
                else if (kind.GetString() == "device_ping")
                {
                    if (!TryReadNonce(root, out var nonce))
                        throw new InvalidDataException("Invalid CLI heartbeat.");
                    await history.SendJsonAsync(new { type = "device_pong", nonce }, token);
                }
                else if (kind.GetString() == "device_pong")
                {
                    if (!TryReadNonce(root, out var nonce) || !heartbeat.Confirm(nonce))
                        throw new InvalidDataException("Unmatched CLI heartbeat.");
                }
            }
        }
    }

    private static void QueueClaim(CliPairingApprovalCoordinator approvals, JsonElement root)
    {
        if (!TryReadIds(root, out var eventId, out var pairId, out var clientId)) return;
        try
        {
            var claim = new VoicePairingClaim(eventId, pairId, clientId,
                root.GetProperty("clientPublicKey").GetString()!,
                root.GetProperty("proof").GetString()!);
            approvals.Queue(claim, DisplayText(root, "accountEmail", 254),
                DisplayText(root, "clientName", 80));
        }
        catch (Exception) { /* Invalid or expired QR proof is rejected without sending its contents anywhere. */ }
    }

    private static string DisplayText(JsonElement root, string name, int limit)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return "";
        var text = value.GetString() ?? "";
        if (text.Length > limit || text.Any(c => char.IsControl(c))) return "";
        return text;
    }

    private static bool TryReadIds(JsonElement root, out string eventId,
        out string pairId, out string clientId)
    {
        eventId = pairId = clientId = "";
        try
        {
            eventId = root.GetProperty("eventId").GetString()!;
            pairId = root.GetProperty("pairId").GetString()!;
            clientId = root.GetProperty("clientId").GetString()!;
            VoicePairingCrypto.ValidateHexId(eventId, nameof(eventId));
            VoicePairingCrypto.ValidateHexId(pairId, nameof(pairId));
            VoicePairingCrypto.ValidateHexId(clientId, nameof(clientId));
            return true;
        }
        catch { return false; }
    }

    private static async Task SendDecisionAsync(CliHistoryWatchManager history, string eventId,
        string pairId, string clientId, bool accepted, CancellationToken token)
    {
        await history.SendJsonAsync(new
        {
            type = accepted ? "accept_pair" : "reject_pair",
            eventId,
            pairId,
            clientId
        }, token);
    }

    private static bool TryReadNonce(JsonElement root, out string nonce)
    {
        nonce = "";
        try
        {
            nonce = root.GetProperty("nonce").GetString()!;
            VoicePairingCrypto.ValidateHexId(nonce, nameof(nonce));
            return true;
        }
        catch { return false; }
    }

    private async Task WaitForNewRegistrationAsync(VoiceDeviceRegistration rejected,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var current = _store.LoadDeviceRegistration();
            if (current is not null && current != rejected) return;
            await _registrationWake.WaitAsync(token);
        }
    }

    private static async Task RunHeartbeatAsync(CliHistoryWatchManager history,
        DeviceHeartbeat heartbeat, CancellationTokenSource connection)
    {
        var token = connection.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), token);
                if (heartbeat.Expired())
                {
                    connection.Cancel();
                    return;
                }
                if (heartbeat.TryBegin(out var nonce))
                    await history.SendJsonAsync(new { type = "device_ping", nonce }, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { connection.Cancel(); }
    }

    private sealed class DeviceHeartbeat
    {
        private readonly object _gate = new();
        private string? _nonce;
        private DateTimeOffset _sentAt;

        internal bool TryBegin(out string nonce)
        {
            lock (_gate)
            {
                nonce = "";
                if (_nonce is not null) return false;
                _nonce = nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
                    .ToLowerInvariant();
                _sentAt = DateTimeOffset.UtcNow;
                return true;
            }
        }

        internal bool Confirm(string nonce)
        {
            lock (_gate)
            {
                if (_nonce != nonce) return false;
                _nonce = null;
                return true;
            }
        }

        internal bool Expired()
        {
            lock (_gate)
                return _nonce is not null && DateTimeOffset.UtcNow - _sentAt >= TimeSpan.FromSeconds(45);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        // The receive loop may still be leaving WaitAsync or the socket; disposing
        // those objects here would turn normal shutdown into a background exception.
    }
}
