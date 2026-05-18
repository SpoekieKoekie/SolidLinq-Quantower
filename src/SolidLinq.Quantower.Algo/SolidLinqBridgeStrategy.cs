using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using SolidLinq.Quantower.CoreApi;
using SolidLinq.Quantower.Models;
using TradingPlatform.BusinessLayer;

namespace SolidLinq.Quantower.Algo;

/// <summary>
/// Quantower Algo strategy: maintains the SolidLinq hub WebSocket, executes TV-normalized commands on the bound Symbol/Account,
/// POSTs <c>execution_ack</c> per dispatch, and reports closed P&amp;L via <c>POST /cbot/execution</c> (same contract as MT5/cTrader/Ninja).
/// </summary>
public sealed class SolidLinqBridgeStrategy : Strategy, ICurrentAccount, ICurrentSymbol
{
    [InputParameter("Symbol", 0)]
    public Symbol CurrentSymbol { get; set; }

    [InputParameter("Account", 1)]
    public Account CurrentAccount { get; set; }

    [InputParameter("WebSocket URL", 2)]
    public string WebSocketUrl { get; set; } = "";

    [InputParameter("Core base URL", 3)]
    public string CoreBaseUrl { get; set; } = "";

    [InputParameter("Bridge instance id", 4)]
    public string BridgeInstanceId { get; set; } = "";

    [InputParameter("Auth token (bridge generate)", 5)]
    public string AuthToken { get; set; } = "";

    [InputParameter("Worker API token", 6)]
    public string WorkerApiToken { get; set; } = "";

    [InputParameter("Hub protocol version", 7, minimum: 1, maximum: 2, increment: 1, decimalPlaces: 0)]
    public int ProtocolVersion { get; set; } = 2;

    public override string[] MonitoringConnectionsIds => new[]
    {
        this.CurrentSymbol?.ConnectionId,
        this.CurrentAccount?.ConnectionId
    };

    private CancellationTokenSource? _cts;
    private Task? _hubTask;
    private HttpClient? _http;
    private readonly ConcurrentDictionary<string, byte> _postedClosedPids = new ConcurrentDictionary<string, byte>();

    public SolidLinqBridgeStrategy()
    {
        this.Name = nameof(SolidLinqBridgeStrategy);
        this.Description = "SolidLinq: WS dispatches → orders; HTTP execution_ack + closed P&L (/cbot/execution).";
    }

    protected override void OnRun()
    {
        if (!ValidateInputs(out var err))
        {
            this.Log(err, StrategyLoggingLevel.Error);
            return;
        }

        this.CurrentSymbol = Core.Instance.GetSymbol(this.CurrentSymbol!.CreateInfo());
        this.CurrentAccount = Core.Instance.GetAccount(this.CurrentAccount!.CreateInfo());

        if (this.CurrentSymbol.ConnectionId != this.CurrentAccount.ConnectionId)
        {
            this.Log("Symbol and Account must use the same connection.", StrategyLoggingLevel.Error);
            return;
        }

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", this.WorkerApiToken.Trim());

        var options = new BridgeOptions
        {
            WebSocketUrl = new Uri(this.WebSocketUrl.Trim()),
            BridgeInstanceId = this.BridgeInstanceId.Trim(),
            AuthToken = this.AuthToken.Trim(),
            CoreBaseUrl = new Uri(this.CoreBaseUrl.Trim().TrimEnd('/') + "/"),
            WorkerApiToken = this.WorkerApiToken.Trim(),
            Platform = "quantower",
            ProtocolVersion = this.ProtocolVersion >= 1 && this.ProtocolVersion <= 2 ? this.ProtocolVersion : 2
        };

        var ack = new CoreAckClient(_http);
        var executor = new QuantowerHubExecutor(this);
        var hub = new HubBridgeClient(options, ack, executor);

        Core.Instance.ClosedPositionAdded += OnClosedPositionAdded;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _hubTask = Task.Run(async () =>
        {
            try
            {
                await hub.RunWithReconnectAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                this.Log($"SolidLinq hub stopped: {ex.Message}", StrategyLoggingLevel.Error);
            }
        }, token);

        this.Log("SolidLinq bridge connected (background task).", StrategyLoggingLevel.Info);
    }

    protected override void OnStop()
    {
        Core.Instance.ClosedPositionAdded -= OnClosedPositionAdded;

        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _hubTask?.GetAwaiter().GetResult();
        }
        catch
        {
        }

        _http?.Dispose();
        _cts?.Dispose();

        _cts = null;
        _hubTask = null;
        _http = null;

        base.OnStop();
    }

    private bool ValidateInputs(out string error)
    {
        error = "";
        if (this.CurrentSymbol == null)
        {
            error = "Symbol is required.";
            return false;
        }
        if (this.CurrentAccount == null)
        {
            error = "Account is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(this.WebSocketUrl))
        {
            error = "WebSocket URL is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(this.CoreBaseUrl))
        {
            error = "Core base URL is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(this.BridgeInstanceId))
        {
            error = "Bridge instance id is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(this.AuthToken))
        {
            error = "Auth token is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(this.WorkerApiToken))
        {
            error = "Worker API token is required.";
            return false;
        }
        return true;
    }

    private void OnClosedPositionAdded(ClosedPosition cp)
    {
        if (this.CurrentSymbol == null || this.CurrentAccount == null) return;
        if (cp.Symbol != this.CurrentSymbol || cp.Account != this.CurrentAccount) return;

        var pid = cp.Id.ToString();
        if (string.IsNullOrWhiteSpace(pid) || !_postedClosedPids.TryAdd(pid, 0)) return;

        var coreBase = this.CoreBaseUrl.Trim().TrimEnd('/') + "/";
        var instanceId = this.BridgeInstanceId.Trim();
        var bearer = this.WorkerApiToken.Trim();

        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                var ack = new CoreAckClient(http);
                var body = ClosedTradePayload.FromClosedPosition(cp);
                await ack.PostClosedTradeAsync(new Uri(coreBase), instanceId, body, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _postedClosedPids.TryRemove(pid, out _);
                this.Log($"POST /cbot/execution failed: {ex.Message}", StrategyLoggingLevel.Error);
            }
        });
    }

    private sealed class QuantowerHubExecutor : IHubOrderExecutor
    {
        private readonly SolidLinqBridgeStrategy _s;

        public QuantowerHubExecutor(SolidLinqBridgeStrategy s) => _s = s;

        public Task<ExecutionResult> ExecuteAsync(HubDispatchMessage cmd, CancellationToken ct)
        {
            ExecutionResult r;
            try
            {
                r = ExecuteSync(cmd);
            }
            catch (Exception ex)
            {
                r = new ExecutionResult(false, "error", null, ex.Message);
            }
            return Task.FromResult(r);
        }

        private ExecutionResult ExecuteSync(HubDispatchMessage cmd)
        {
            var sym = _s.CurrentSymbol;
            var acc = _s.CurrentAccount;
            if (sym == null || acc == null)
                return new ExecutionResult(false, "no-symbol-account", null, null);

            var incoming = (cmd.Symbol ?? "").Trim();
            if (incoming.Length > 0 &&
                !string.Equals(incoming, sym.Name, StringComparison.OrdinalIgnoreCase))
            {
                return new ExecutionResult(false, "symbol-mismatch", null, $"Expected {sym.Name}, got {incoming}");
            }

            var kind = (cmd.Type ?? "order").Trim().ToLowerInvariant();
            if (kind == "close") return ClosePositions(cmd, sym, acc);

            return PlaceOrder(cmd, sym, acc);
        }

        private static ExecutionResult ClosePositions(HubDispatchMessage cmd, Symbol sym, Account acc)
        {
            var positions = Core.Instance.Positions.Where(p => p.Symbol == sym && p.Account == acc).ToArray();
            if (positions.Length == 0)
                return new ExecutionResult(true, "no-positions", null, null);

            var remaining = cmd.Lots > 0 ? cmd.Lots : double.PositiveInfinity;
            var anyFail = false;
            foreach (var p in positions)
            {
                var q = remaining >= p.Quantity ? p.Quantity : remaining;
                if (q <= 0) continue;
                var res = q >= p.Quantity ? p.Close() : p.Close(q);
                if (res.Status == TradingOperationResultStatus.Failure) anyFail = true;
                if (!double.IsPositiveInfinity(remaining)) remaining -= q;
            }

            return new ExecutionResult(!anyFail, anyFail ? "close-refused" : "close-ok", null, null);
        }

        private static ExecutionResult PlaceOrder(HubDispatchMessage cmd, Symbol sym, Account acc)
        {
            var side = ParseSide(cmd.Side) ?? Side.Buy;
            var qty = cmd.Lots > 0 ? cmd.Lots : 0;
            if (qty <= 0)
                return new ExecutionResult(false, "zero-lots", null, null);

            var ot = (cmd.OrderType ?? "market").Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");
            if (ot is "market" or "mkt")
            {
                var marketType = Core.OrderTypes.FirstOrDefault(x =>
                    x.ConnectionId == sym.ConnectionId && x.Behavior == OrderTypeBehavior.Market);
                if (marketType == null || string.IsNullOrEmpty(marketType.Id))
                    return new ExecutionResult(false, "no-market-type", null, null);

                var res = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
                {
                    Account = acc,
                    Symbol = sym,
                    OrderTypeId = marketType.Id,
                    Quantity = qty,
                    Side = side,
                    TimeInForce = TimeInForce.Default
                });

                var ok = res.Status != TradingOperationResultStatus.Failure;
                return new ExecutionResult(ok, ok ? "submitted" : (res.Message ?? "failed"), null, null);
            }

            return new ExecutionResult(false, "unsupported-order-type", null, $"orderType={cmd.OrderType}");
        }

        private static Side? ParseSide(string? raw)
        {
            var s = (raw ?? "").Trim().ToLowerInvariant();
            if (s is "sell" or "short") return Side.Sell;
            if (s is "buy" or "long") return Side.Buy;
            return null;
        }
    }

    private static class ClosedTradePayload
    {
        public static Dictionary<string, object?> FromClosedPosition(ClosedPosition cp)
        {
            var item = cp.NetPnL;
            var pnl = item == null ? 0 : item.Value;
            return new Dictionary<string, object?>
            {
                ["pid"] = cp.Id.ToString(),
                ["pnl"] = pnl,
                ["netProfit"] = pnl,
                ["symbol"] = cp.Symbol?.Name,
                ["side"] = cp.Side == Side.Buy ? "buy" : "sell",
                ["platform"] = "quantower",
                ["closedAt"] = DateTime.UtcNow.ToString("O")
            };
        }
    }
}
