using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SolidLinq.Quantower.CoreApi;
using SolidLinq.Quantower.Models;

namespace SolidLinq.Quantower;

/// <summary>Connects to SolidLinq hub WebSocket, sends hello, receives dispatch JSON. See LinkStateHub (Core).</summary>
public sealed class HubBridgeClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonParse = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    private readonly BridgeOptions _options;
    private readonly CoreAckClient _ackClient;
    private readonly IHubOrderExecutor _executor;
    /// <summary>Same hub command id may be ignored only for 30s (not permanently).</summary>
    private readonly TimedCommandDedupe _commandDedupe = new(TimeSpan.FromSeconds(30));
    private ClientWebSocket? _activeSocket;

    public HubBridgeClient(BridgeOptions options, CoreAckClient ackClient, IHubOrderExecutor executor)
    {
        _options = options;
        _ackClient = ackClient;
        _executor = executor;
    }

    /// <summary>Runs until cancellation, reconnecting with backoff on socket errors.</summary>
    public async Task RunWithReconnectAsync(CancellationToken ct)
    {
        var delayMs = 1000;
        const int maxDelayMs = 60_000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(ct).ConfigureAwait(false);
                delayMs = 1000;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (WebSocketException)
            {
            }
            catch (IOException)
            {
            }
            catch (HttpRequestException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            if (ct.IsCancellationRequested) return;
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
            delayMs = Math.Min(maxDelayMs, delayMs * 2);
        }
    }

    public bool TrySendPositionClosedMirror(string json)
    {
        var ws = _activeSocket;
        if (ws == null || ws.State != WebSocketState.Open) return false;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            _ = ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task RunSessionAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(_options.WebSocketUrl, ct).ConfigureAwait(false);
        _activeSocket = ws;
        HubPnlMirror.TrySendCore = TrySendPositionClosedMirror;

        var hello = new Dictionary<string, object?>
        {
            ["type"] = "hello",
            ["protocolVersion"] = _options.ProtocolVersion,
            ["bridgeInstanceId"] = _options.BridgeInstanceId,
            ["authToken"] = _options.AuthToken,
            ["platform"] = _options.Platform
        };
        try
        {
            await SendJsonAsync(ws, hello, ct).ConfigureAwait(false);

            var buffer = new byte[64 * 1024];
            while (!ct.IsCancellationRequested)
            {
                var ms = new MemoryStream();
                WebSocketReceiveResult seg;
                do
                {
                    seg = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (seg.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, seg.Count);
                } while (!seg.EndOfMessage);

                var text = Encoding.UTF8.GetString(ms.ToArray());
                await HandleIncomingAsync(ws, text, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (ReferenceEquals(_activeSocket, ws))
            {
                _activeSocket = null;
                HubPnlMirror.TrySendCore = null;
            }
        }
    }

    private async Task HandleIncomingAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var tEl) ? tEl.GetString()?.ToLowerInvariant() : null;
        if (type is "hello_ack" or "pong" or "batch_ack" or "error") return;

        if (type is "ping")
        {
            await SendJsonAsync(ws, new Dictionary<string, object?> { ["type"] = "pong" }, ct).ConfigureAwait(false);
            return;
        }

        if (type is "server_ping")
        {
            await SendJsonAsync(ws, new Dictionary<string, object?> { ["type"] = "pong" }, ct).ConfigureAwait(false);
            return;
        }

        var cmd = JsonSerializer.Deserialize<HubDispatchMessage>(text, JsonParse);
        if (cmd == null || string.IsNullOrWhiteSpace(cmd.Id)) return;

        var kind = (cmd.Type ?? "order").Trim().ToLowerInvariant();
        var dedupeKey = $"{cmd.Id!.Trim()}:{kind}";
        if (!_commandDedupe.TryAccept(dedupeKey))
            return;

        var sw = Stopwatch.StartNew();
        ExecutionResult result;
        try
        {
            result = await _executor.ExecuteAsync(cmd, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new ExecutionResult(false, ex.Message, null, null);
        }

        sw.Stop();
        await _ackClient.PostExecutionAckAsync(
            _options.CoreBaseUrl,
            cmd.Id!,
            result.Ok,
            result.Status,
            sw.Elapsed.TotalMilliseconds,
            sw.Elapsed.TotalMilliseconds,
            _options.BridgeInstanceId,
            _options.Platform,
            ct).ConfigureAwait(false);

        if (result.ClosedTradePayload is not null)
        {
            await _ackClient.PostClosedTradeAsync(
                    _options.CoreBaseUrl,
                    _options.BridgeInstanceId,
                    result.ClosedTradePayload,
                    _options.AuthToken,
                    ct)
                .ConfigureAwait(false);
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => default;
}

public readonly struct ExecutionResult
{
    public ExecutionResult(bool ok, string? status, object? closedTradePayload, string? errorMessage)
    {
        Ok = ok;
        Status = status;
        ClosedTradePayload = closedTradePayload;
        ErrorMessage = errorMessage;
    }

    public bool Ok { get; }
    public string? Status { get; }
    public object? ClosedTradePayload { get; }
    public string? ErrorMessage { get; }
}

/// <summary>Binds hub commands to broker operations (stub, Quantower algo, tests).</summary>
public interface IHubOrderExecutor
{
    Task<ExecutionResult> ExecuteAsync(HubDispatchMessage cmd, CancellationToken ct);
}

public sealed class LoggingStubExecutor : IHubOrderExecutor
{
    public Task<ExecutionResult> ExecuteAsync(HubDispatchMessage cmd, CancellationToken ct)
    {
        Console.WriteLine(
            "[stub] {0} {1} {2} lots={3} orderType={4} id={5}",
            cmd.Type,
            cmd.Side,
            cmd.Symbol,
            cmd.Lots,
            cmd.OrderType,
            cmd.Id);
        return Task.FromResult(new ExecutionResult(true, "stub-ok", null, null));
    }
}
