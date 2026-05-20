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
/// Quantower Algo: SolidLinq hub WebSocket, orders on bound Symbol/Account, HTTP callbacks.
/// By default uses production <c>https://core.solidlinq.com</c> and
/// <c>wss://ws.solidlinq.com/ws/bridge/{BridgeInstanceId}</c> (see Core <c>BRIDGE_WS_PROTOCOL.md</c>).
/// Set <see cref="UseBuiltInSolidLinqProductionEndpoints"/> false for JSON or custom compiled URLs.
/// HTTP uses the bridge <see cref="AuthToken"/> only (no worker API token).
/// </summary>
public sealed class SolidLinqBridgeStrategy : Strategy, ICurrentAccount, ICurrentSymbol
{
    /// <summary>
    /// When true, ignores <c>quantower-bridge.json</c> and custom hard-coded URL strings; Core + WS hosts match production SolidLinq.
    /// Set false to use <see cref="HardCodedWebSocketUrl"/> + <see cref="HardCodedCoreBaseUrl"/> or the JSON file.
    /// </summary>
    private const bool UseBuiltInSolidLinqProductionEndpoints = true;

    /// <summary>Production Core REST origin.</summary>
    private const string BuiltInSolidLinqCoreBaseUrl = "https://core.solidlinq.com";

    /// <summary>WS path prefix; resolved URL is prefix + <see cref="BridgeInstanceId"/>.</summary>
    private const string BuiltInSolidLinqWebSocketBridgePrefix = "wss://ws.solidlinq.com/ws/bridge/";

    /// <summary>
    /// Used only when <see cref="UseBuiltInSolidLinqProductionEndpoints"/> is false. Set both this and <see cref="HardCodedCoreBaseUrl"/>
    /// non-empty to use compiled URLs (e.g. staging). Example: <c>wss://ws.example.com/ws/your-path</c>
    /// </summary>
    private const string HardCodedWebSocketUrl = "";

    /// <summary>Used only when <see cref="UseBuiltInSolidLinqProductionEndpoints"/> is false.</summary>
    private const string HardCodedCoreBaseUrl = "";

    /// <summary>Logged on start so you can confirm Quantower loaded the latest DLL.</summary>
    private const string StrategyBuildId = "2026-05-20-pnl-fix";

    private const int HubProtocolVersion = 2;

    [InputParameter("Symbol", 0)]
    public Symbol CurrentSymbol { get; set; } = null!;

    [InputParameter("Account", 1)]
    public Account CurrentAccount { get; set; } = null!;

    [InputParameter("Bridge Instance Id", 2)]
    public string BridgeInstanceId { get; set; } = "";

    [InputParameter("Auth Token", 3)]
    public string AuthToken { get; set; } = "";

    [InputParameter("Lot Multiplier", 4, 0.01, 100.0, 0.01, 2)]
    public double LotMultiplier { get; set; } = 1.0;

    [InputParameter("SL In Payload", 5)]
    public bool SlInPayload { get; set; } = true;

    [InputParameter("SL Mode", 6)]
    public BridgeSlMode StopLossMode { get; set; } = BridgeSlMode.UseStrategySl;

    [InputParameter("SL Multiplier", 7, 0.01, 100.0, 0.01, 2)]
    public double SlMultiplier { get; set; } = 1.0;

    [InputParameter("TP In Payload", 8)]
    public bool TpInPayload { get; set; } = true;

    [InputParameter("TP Multiplier", 9, 0.01, 100.0, 0.01, 2)]
    public double TpMultiplier { get; set; } = 1.0;

    [InputParameter("Daily Loss Limit", 10, 0.0, 1e12, 1.0, 2)]
    public double DailyLossLimit { get; set; }

    [InputParameter("Weekly Loss Limit", 11, 0.0, 1e12, 1.0, 2)]
    public double WeeklyLossLimit { get; set; }

    [InputParameter("Daily Profit Target", 12, 0.0, 1e12, 1.0, 2)]
    public double DailyProfitTarget { get; set; }

    [InputParameter("Weekly Profit Target", 13, 0.0, 1e12, 1.0, 2)]
    public double WeeklyProfitTarget { get; set; }

    [InputParameter("Stop Taking New Trades When Hit", 14)]
    public bool StopTakingNewTradesWhenHit { get; set; } = true;

    [InputParameter("Close Open Positions When Hit", 15)]
    public bool CloseOpenPositionsWhenHit { get; set; } = true;

    [InputParameter("Enable Overall Max Loss", 16)]
    public bool EnableOverallMaxLoss { get; set; }

    [InputParameter("Overall Max Loss", 17, 0.0, 1e12, 1.0, 2)]
    public double OverallMaxLoss { get; set; }

    [InputParameter("Enable Overall Max Profit", 18)]
    public bool EnableOverallMaxProfit { get; set; }

    [InputParameter("Overall Max Profit", 19, 0.0, 1e12, 1.0, 2)]
    public double OverallMaxProfit { get; set; }

    [InputParameter("Drawdown Mode", 20)]
    public BridgeDrawdownMode AccountDrawdownMode { get; set; } = BridgeDrawdownMode.Static;

    [InputParameter("Use Equity For Drawdown", 21)]
    public bool UseEquityForDrawdown { get; set; } = true;

    [InputParameter("Manual Unlock Required For Overall Hit", 22)]
    public bool ManualUnlockRequiredForOverallHit { get; set; } = true;

    public override string[] MonitoringConnectionsIds =>
        new[] { this.CurrentSymbol?.ConnectionId, this.CurrentAccount?.ConnectionId }
            .Where(static s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToArray();

    private CancellationTokenSource? _cts;
    private Task? _hubTask;
    private Task? _riskTask;
    private HttpClient? _http;
    private string _resolvedCoreBase = "";
    private readonly ConcurrentDictionary<string, double> _postedClosedPids = new();
    private readonly BridgeRiskCoordinator _risk = new();
    private readonly QuantowerMainThreadDispatcher _mainThread = new();
    private string _httpBearer = "";

    public SolidLinqBridgeStrategy()
    {
        this.Name = nameof(SolidLinqBridgeStrategy);
        this.Description =
            $"SolidLinq bridge ({StrategyBuildId}). Production WS/Core built-in; cBot-style SL/TP and risk limits.";
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

        string wsRaw;
        string coreRaw;
        if (UseBuiltInSolidLinqProductionEndpoints)
        {
            var id = this.BridgeInstanceId.Trim();
            wsRaw = BuiltInSolidLinqWebSocketBridgePrefix + id.TrimStart('/');
            coreRaw = BuiltInSolidLinqCoreBaseUrl;
            this.Log(
                "Using built-in SolidLinq production endpoints (core.solidlinq.com + ws.solidlinq.com/ws/bridge/…).",
                StrategyLoggingLevel.Info);
        }
        else
        {
            var hw = HardCodedWebSocketUrl.Trim();
            var hc = HardCodedCoreBaseUrl.Trim();
            var useCustomHardCoded = !string.IsNullOrWhiteSpace(hw) || !string.IsNullOrWhiteSpace(hc);
            if (useCustomHardCoded)
            {
                if (string.IsNullOrWhiteSpace(hw) || string.IsNullOrWhiteSpace(hc))
                {
                    this.Log(
                        "HardCodedWebSocketUrl and HardCodedCoreBaseUrl must both be set in SolidLinqBridgeStrategy.cs, or leave both empty to use quantower-bridge.json.",
                        StrategyLoggingLevel.Error);
                    return;
                }

                wsRaw = hw;
                coreRaw = hc;
                this.Log("Using custom hard-coded WebSocket and Core base URL from SolidLinqBridgeStrategy.cs.", StrategyLoggingLevel.Info);
            }
            else
            {
                var cfg = SolidLinqBridgeEndpointsConfig.TryLoad(null, out var cfgPath, out var cfgErr);
                if (cfg == null)
                {
                    this.Log(cfgErr ?? "Missing bridge config.", StrategyLoggingLevel.Error);
                    this.Log($"Expected JSON with webSocketUrl and coreBaseUrl at: {cfgPath}", StrategyLoggingLevel.Info);
                    return;
                }

                wsRaw = (cfg.WebSocketUrl ?? "").Trim();
                coreRaw = (cfg.CoreBaseUrl ?? "").Trim();
                if (string.IsNullOrWhiteSpace(wsRaw) || string.IsNullOrWhiteSpace(coreRaw))
                {
                    this.Log($"Bridge config must set webSocketUrl and coreBaseUrl: {cfgPath}", StrategyLoggingLevel.Error);
                    return;
                }
            }
        }

        _resolvedCoreBase = coreRaw.TrimEnd('/') + "/";

        _httpBearer = this.AuthToken.Trim();
        _http = new HttpClient();
        if (!string.IsNullOrWhiteSpace(_httpBearer))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _httpBearer);

        _risk.DailyLossLimit = this.DailyLossLimit;
        _risk.WeeklyLossLimit = this.WeeklyLossLimit;
        _risk.DailyProfitTarget = this.DailyProfitTarget;
        _risk.WeeklyProfitTarget = this.WeeklyProfitTarget;
        _risk.StopTakingNewTradesWhenHit = this.StopTakingNewTradesWhenHit;
        _risk.CloseOpenPositionsWhenHit = this.CloseOpenPositionsWhenHit;
        _risk.EnableOverallMaxLoss = this.EnableOverallMaxLoss;
        _risk.OverallMaxLoss = this.OverallMaxLoss;
        _risk.EnableOverallMaxProfit = this.EnableOverallMaxProfit;
        _risk.OverallMaxProfit = this.OverallMaxProfit;
        _risk.UseEquityForDrawdown = this.UseEquityForDrawdown;
        _risk.DrawdownMode = this.AccountDrawdownMode;
        _risk.ManualUnlockRequiredForOverallHit = this.ManualUnlockRequiredForOverallHit;
        _risk.Bind(this.CurrentAccount, this.CurrentSymbol);
        _risk.EnsureStarted(DateTime.UtcNow);

        var options = new BridgeOptions
        {
            WebSocketUrl = new Uri(wsRaw),
            BridgeInstanceId = this.BridgeInstanceId.Trim(),
            AuthToken = this.AuthToken.Trim(),
            CoreBaseUrl = new Uri(coreRaw.TrimEnd('/') + "/"),
            Platform = "quantower",
            ProtocolVersion = HubProtocolVersion
        };

        _mainThread.Bind(this.CurrentSymbol);

        var ack = new CoreAckClient(_http);
        var executor = new QuantowerHubExecutor(this, _risk, _mainThread, (msg, level) => this.Log(msg, level));
        var hub = new HubBridgeClient(options, ack, executor);

        Core.Instance.ClosedPositionAdded += OnClosedPositionAdded;
        Core.Instance.TradeAdded += OnTradeAdded;

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

        _riskTask = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    _mainThread.DrainPending();
                    _risk.Poll((msg, level) => this.Log(msg, level));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                /* ignore */
            }
        }, token);

        this.Log($"SolidLinq bridge started (build {StrategyBuildId}).", StrategyLoggingLevel.Info);
    }

    protected override void OnStop()
    {
        Core.Instance.ClosedPositionAdded -= OnClosedPositionAdded;
        Core.Instance.TradeAdded -= OnTradeAdded;

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

        try
        {
            _riskTask?.GetAwaiter().GetResult();
        }
        catch
        {
        }

        _http?.Dispose();
        _cts?.Dispose();

        _cts = null;
        _hubTask = null;
        _riskTask = null;
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
        return true;
    }

    private void OnClosedPositionAdded(ClosedPosition cp)
    {
        if (this.CurrentSymbol == null || this.CurrentAccount == null) return;
        if (!ClosedPnlReporter.MatchesBinding(cp, this.CurrentSymbol, this.CurrentAccount))
            return;

        // Manual close, SL/TP hit, or platform flatten — hub path already cleans, this covers the rest.
        ProtectiveOrderCleanup.CancelOrphanedForSymbol(
            this.CurrentSymbol,
            this.CurrentAccount,
            (msg, level) => this.Log(msg, level));

        _risk.NoteClosedRealized(ClosedPnlReporter.ReadNetPnlForRisk(cp), DateTime.UtcNow);

        var coreBase = _resolvedCoreBase;
        if (string.IsNullOrWhiteSpace(coreBase) || _http == null) return;

        var instanceId = this.BridgeInstanceId.Trim();
        var token = this.AuthToken.Trim();

        _ = Task.Run(async () =>
        {
            await ClosedPnlReporter.ReportAsync(
                cp,
                instanceId,
                coreBase,
                token,
                _http,
                _postedClosedPids,
                (msg, level) => this.Log(msg, level)).ConfigureAwait(false);
        });
    }

    private void PostPnlAfterBridgeClose(Symbol sym, Account acc)
    {
        if (string.IsNullOrWhiteSpace(_resolvedCoreBase) || _http == null) return;

        ClosedPnlReporter.SchedulePollAfterBridgeClose(
            sym,
            acc,
            this.BridgeInstanceId.Trim(),
            _resolvedCoreBase,
            this.AuthToken.Trim(),
            _http,
            _postedClosedPids,
            (msg, level) => this.Log(msg, level));
    }

    private void OnTradeAdded(Trade trade)
    {
        if (this.CurrentSymbol == null || this.CurrentAccount == null) return;
        if (trade.PositionImpactType != PositionImpactType.Close) return;
        if (!ClosedPnlReporter.MatchesTrade(trade, this.CurrentSymbol, this.CurrentAccount)) return;

        ProtectiveOrderCleanup.CancelOrphanedForSymbol(
            this.CurrentSymbol,
            this.CurrentAccount,
            (msg, level) => this.Log(msg, level));

        if (_http == null) return;

        var coreBase = _resolvedCoreBase;
        if (string.IsNullOrWhiteSpace(coreBase)) return;

        _ = Task.Run(async () =>
        {
            await ClosedPnlReporter.ReportFromTradeAsync(
                trade,
                this.BridgeInstanceId.Trim(),
                coreBase,
                this.AuthToken.Trim(),
                _http,
                _postedClosedPids,
                (msg, level) => this.Log(msg, level)).ConfigureAwait(false);
        });
    }

    private void ReportSnapshotsAfterClose(System.Collections.Generic.IReadOnlyList<PositionCloseSnapshot> snapshots)
    {
        if (snapshots.Count == 0 || string.IsNullOrWhiteSpace(_resolvedCoreBase) || _http == null) return;

        var instanceId = this.BridgeInstanceId.Trim();
        var token = this.AuthToken.Trim();
        var coreBase = _resolvedCoreBase;

        _ = Task.Run(async () =>
        {
            foreach (var snap in snapshots)
            {
                await ClosedPnlReporter.ReportFromSnapshotAsync(
                    snap,
                    instanceId,
                    coreBase,
                    token,
                    _http,
                    _postedClosedPids,
                    (msg, level) => this.Log(msg, level)).ConfigureAwait(false);
            }
        });
    }

    private sealed class QuantowerHubExecutor : IHubOrderExecutor
    {
        private readonly SolidLinqBridgeStrategy _s;
        private readonly BridgeRiskCoordinator _risk;
        private readonly QuantowerMainThreadDispatcher _dispatch;
        private readonly Action<string, StrategyLoggingLevel> _log;

        public QuantowerHubExecutor(
            SolidLinqBridgeStrategy s,
            BridgeRiskCoordinator risk,
            QuantowerMainThreadDispatcher dispatch,
            Action<string, StrategyLoggingLevel> log)
        {
            _s = s;
            _risk = risk;
            _dispatch = dispatch;
            _log = log;
        }

        public Task<ExecutionResult> ExecuteAsync(HubDispatchMessage cmd, CancellationToken ct) =>
            _dispatch.RunAsync(() => ExecuteSync(cmd), ct);

        private ExecutionResult ExecuteSync(HubDispatchMessage cmd)
        {
            var sym = _s.CurrentSymbol;
            var acc = _s.CurrentAccount;
            if (sym == null || acc == null)
                return new ExecutionResult(false, "no-symbol-account", null, null);

            var kind = (cmd.Type ?? "order").Trim().ToLowerInvariant();
            var id = cmd.Id ?? "";
            _log(
                $"Hub {kind} id={id} {cmd.Side} {cmd.Symbol} lots={cmd.Lots} sl%={cmd.SlPercent ?? cmd.SlPct} tp%={cmd.TpPercent ?? cmd.TpPct}",
                StrategyLoggingLevel.Trading);

            var incoming = (cmd.Symbol ?? "").Trim();
            if (incoming.Length > 0 &&
                !string.Equals(incoming, sym.Name, StringComparison.OrdinalIgnoreCase))
            {
                return new ExecutionResult(false, "symbol-mismatch", null, $"Expected {sym.Name}, got {incoming}");
            }

            if (kind == "close")
                return ClosePositions(cmd, sym, acc);

            if (!_risk.TryEnterNewOrder(out var blockReason))
            {
                _log($"Order blocked: {blockReason}", StrategyLoggingLevel.Error);
                return new ExecutionResult(false, "risk-locked", null, blockReason);
            }

            return PlaceOrder(cmd, sym, acc);
        }

        private ExecutionResult ClosePositions(HubDispatchMessage cmd, Symbol sym, Account acc)
        {
            var positions = Core.Instance.Positions.Where(p => p.Symbol == sym && p.Account == acc).ToArray();
            if (positions.Length == 0)
            {
                _log("Close: no open positions", StrategyLoggingLevel.Info);
                return new ExecutionResult(true, "no-positions", null, null);
            }

            var remaining = cmd.Lots > 0 ? SymbolLotNormalizer.NormalizeLotsUp(sym, cmd.Lots) : double.PositiveInfinity;
            var anyFail = false;
            var anyClosed = false;
            var snapshots = new System.Collections.Generic.List<PositionCloseSnapshot>();
            var hubId = cmd.Id ?? "";

            foreach (var p in positions)
            {
                var q = remaining >= p.Quantity ? p.Quantity : remaining;
                if (q <= 0) continue;
                q = SymbolLotNormalizer.NormalizeLotsUp(sym, q);
                var fullClose = q >= p.Quantity - 1e-9;

                PositionCloseSnapshot? snap = null;
                if (fullClose)
                    snap = PositionCloseSnapshot.TryCapture(p, sym, hubId);

                if (fullClose)
                    ProtectiveOrderCleanup.CancelBracketsForPosition(p, _log);

                var res = fullClose ? p.Close() : p.Close(q);
                if (res.Status == TradingOperationResultStatus.Failure)
                {
                    anyFail = true;
                    _log($"Close failed: {res.Message}", StrategyLoggingLevel.Error);
                }
                else
                {
                    anyClosed = true;
                    _log($"Closed {q} on {sym.Name}", StrategyLoggingLevel.Trading);
                    if (snap != null)
                        snapshots.Add(snap);
                }

                if (!double.IsPositiveInfinity(remaining)) remaining -= q;
            }

            if (anyClosed)
            {
                ProtectiveOrderCleanup.CancelOrphanedForSymbol(sym, acc, _log);
                _s.ReportSnapshotsAfterClose(snapshots);
                _s.PostPnlAfterBridgeClose(sym, acc);
            }

            return new ExecutionResult(!anyFail, anyFail ? "close-refused" : "close-ok", null, null);
        }

        private ExecutionResult PlaceOrder(HubDispatchMessage cmd, Symbol sym, Account acc)
        {
            var side = ParseSide(cmd.Side) ?? Side.Buy;
            var rawLots = cmd.Lots > 0 ? cmd.Lots : 0;
            var lotMul = _s.LotMultiplier;
            if (rawLots > 0 && lotMul > 0 && double.IsFinite(lotMul) && Math.Abs(lotMul - 1.0) > 1e-9)
                rawLots *= lotMul;

            var qty = SymbolLotNormalizer.NormalizeLotsUp(sym, rawLots);
            if (qty <= 0)
                return new ExecutionResult(false, "zero-lots", null, $"raw={cmd.Lots}");

            var ot = (cmd.OrderType ?? "market").Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");
            if (ot is not ("market" or "mkt"))
                return new ExecutionResult(false, "unsupported-order-type", null, $"orderType={cmd.OrderType}");

            var marketType = Core.OrderTypes.FirstOrDefault(x =>
                x.ConnectionId == sym.ConnectionId && x.Behavior == OrderTypeBehavior.Market);
            if (marketType == null || string.IsNullOrEmpty(marketType.Id))
                return new ExecutionResult(false, "no-market-type", null, null);

            var bid = SafeBid(sym);
            var ask = SafeAsk(sym);
            if (bid <= 0 && ask <= 0)
                return new ExecutionResult(false, "no-price", null, "Symbol bid/ask unavailable");

            var req = new PlaceOrderRequestParameters
            {
                Account = acc,
                Symbol = sym,
                OrderTypeId = marketType.Id,
                Quantity = qty,
                Side = side,
                TimeInForce = TimeInForce.Default
            };

            SlTpFromPayload.Apply(req, cmd, sym, side, bid, ask, _s.StopLossMode, _s.SlInPayload, _s.TpInPayload,
                _s.SlMultiplier, _s.TpMultiplier);

            var res = Core.Instance.PlaceOrder(req);
            var ok = res.Status != TradingOperationResultStatus.Failure;
            var slTpNote = req.StopLoss != null || req.TakeProfit != null ? " with SL/TP" : "";
            if (ok)
                _log($"Order placed {side} {qty} {sym.Name}{slTpNote}", StrategyLoggingLevel.Trading);
            else
                _log($"Order failed {side} {qty} {sym.Name}: {res.Message}", StrategyLoggingLevel.Error);

            return new ExecutionResult(ok, ok ? "submitted" : (res.Message ?? "failed"), null, null);
        }

        private static double SafeBid(Symbol s)
        {
            try { return s.Bid; }
            catch { return 0; }
        }

        private static double SafeAsk(Symbol s)
        {
            try { return s.Ask; }
            catch { return 0; }
        }

        private static Side? ParseSide(string? raw)
        {
            var x = (raw ?? "").Trim().ToLowerInvariant();
            if (x is "sell" or "short") return Side.Sell;
            if (x is "buy" or "long") return Side.Buy;
            return null;
        }
    }

}
