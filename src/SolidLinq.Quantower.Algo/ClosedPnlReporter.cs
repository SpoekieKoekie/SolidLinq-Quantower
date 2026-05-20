using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SolidLinq.Quantower;
using SolidLinq.Quantower.CoreApi;
using TradingPlatform.BusinessLayer;

namespace SolidLinq.Quantower.Algo;

internal enum PnlPostSource
{
    Snapshot,
    Trade,
    ClosedPosition
}

internal static class ClosedPnlReporter
{
    private const double PnlEpsilon = 0.000_1;

    public static bool MatchesBinding(ClosedPosition cp, Symbol sym, Account acc)
    {
        if (cp.Symbol == null || cp.Account == null) return false;
        return SymbolNamesMatch(cp.Symbol.Name, sym.Name) && AccountsMatch(cp.Account, acc);
    }

    public static bool MatchesTrade(Trade trade, Symbol sym, Account acc)
    {
        if (trade.Symbol == null || trade.Account == null) return false;
        return SymbolNamesMatch(trade.Symbol.Name, sym.Name) && AccountsMatch(trade.Account, acc);
    }

    public static async Task ReportAsync(
        ClosedPosition cp,
        string bridgeInstanceId,
        string coreBaseUrl,
        string authToken,
        HttpClient http,
        ConcurrentDictionary<string, double> dedupe,
        Action<string, StrategyLoggingLevel> log,
        CancellationToken ct = default)
    {
        var pid = ResolvePid(cp);
        if (string.IsNullOrWhiteSpace(pid))
        {
            log("P&L skip: empty position id", StrategyLoggingLevel.Error);
            return;
        }

        var net = ReadNetPnl(cp);
        var gross = ReadGrossPnl(cp, net);
        await PostBodiesAsync(
            pid,
            cp.Symbol?.Name ?? "",
            cp.Side == Side.Buy ? "buy" : "sell",
            cp.Quantity,
            cp.OpenPrice,
            cp.CurrentPrice,
            net,
            gross,
            cp.OpenTime,
            bridgeInstanceId,
            coreBaseUrl,
            authToken,
            http,
            dedupe,
            log,
            PnlPostSource.ClosedPosition,
            hubCommandId: null,
            ct).ConfigureAwait(false);
    }

    public static Task ReportFromSnapshotAsync(
        PositionCloseSnapshot snap,
        string bridgeInstanceId,
        string coreBaseUrl,
        string authToken,
        HttpClient http,
        ConcurrentDictionary<string, double> dedupe,
        Action<string, StrategyLoggingLevel> log,
        CancellationToken ct = default) =>
        PostBodiesAsync(
            snap.Pid,
            snap.SymbolName,
            snap.Side,
            snap.Quantity,
            snap.OpenPrice,
            snap.ExitPrice,
            snap.NetPnl,
            snap.GrossPnl,
            snap.OpenTime,
            bridgeInstanceId,
            coreBaseUrl,
            authToken,
            http,
            dedupe,
            log,
            PnlPostSource.Snapshot,
            string.IsNullOrWhiteSpace(snap.HubCommandId) ? null : snap.HubCommandId.Trim(),
            ct);

    public static Task ReportFromTradeAsync(
        Trade trade,
        string bridgeInstanceId,
        string coreBaseUrl,
        string authToken,
        HttpClient http,
        ConcurrentDictionary<string, double> dedupe,
        Action<string, StrategyLoggingLevel> log,
        CancellationToken ct = default)
    {
        var pid = (trade.PositionId ?? trade.Id ?? "").Trim();
        if (pid.Length == 0) pid = Guid.NewGuid().ToString("N");

        double net = 0, gross = 0;
        try
        {
            net = trade.NetPnl?.Value ?? 0;
        }
        catch
        {
            /* ignore */
        }

        try
        {
            gross = trade.GrossPnl?.Value ?? net;
        }
        catch
        {
            gross = net;
        }

        if (Math.Abs(net) < PnlEpsilon)
            return PostBodiesWithTradeRetryAsync(
                trade, pid, net, gross, bridgeInstanceId, coreBaseUrl, authToken, http, dedupe, log, ct);

        return PostBodiesAsync(
            pid,
            trade.Symbol?.Name ?? "",
            trade.Side == Side.Buy ? "buy" : "sell",
            trade.Quantity,
            0,
            trade.Price,
            net,
            gross,
            trade.DateTime,
            bridgeInstanceId,
            coreBaseUrl,
            authToken,
            http,
            dedupe,
            log,
            PnlPostSource.Trade,
            hubCommandId: null,
            ct);
    }

    private static double ResolveEffectivePnl(double netPnl, double grossPnl)
    {
        if (Math.Abs(netPnl) >= PnlEpsilon) return netPnl;
        if (Math.Abs(grossPnl) >= PnlEpsilon) return grossPnl;
        return netPnl;
    }

    /// <summary>Allow repost when a prior zero snapshot blocked the real CQG gross P/L.</summary>
    private static bool TryClaimPid(
        ConcurrentDictionary<string, double> dedupe,
        string pid,
        double effectivePnl,
        out bool upgraded)
    {
        upgraded = false;
        while (true)
        {
            if (dedupe.TryGetValue(pid, out var prev))
            {
                if (Math.Abs(effectivePnl) < PnlEpsilon && Math.Abs(prev) >= PnlEpsilon)
                    return false;
                if (Math.Abs(effectivePnl) < PnlEpsilon && Math.Abs(prev) < PnlEpsilon)
                    return false;
                if (Math.Abs(effectivePnl) <= Math.Abs(prev) + PnlEpsilon)
                    return false;
                upgraded = true;
                if (dedupe.TryUpdate(pid, effectivePnl, prev))
                    return true;
                continue;
            }

            if (dedupe.TryAdd(pid, effectivePnl))
                return true;
        }
    }

    private static async Task PostBodiesAsync(
        string pid,
        string symbol,
        string side,
        double quantity,
        double openPrice,
        double exitPrice,
        double netPnl,
        double grossPnl,
        DateTime openTime,
        string bridgeInstanceId,
        string coreBaseUrl,
        string authToken,
        HttpClient http,
        ConcurrentDictionary<string, double> dedupe,
        Action<string, StrategyLoggingLevel> log,
        PnlPostSource source,
        string? hubCommandId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pid))
        {
            log("P&L skip: empty position id", StrategyLoggingLevel.Error);
            return;
        }

        var effectivePnl = ResolveEffectivePnl(netPnl, grossPnl);

        if (source == PnlPostSource.Snapshot && Math.Abs(effectivePnl) < PnlEpsilon)
        {
            log($"P&L defer snapshot pid={pid} (pnl not ready — waiting for trade/closed row)", StrategyLoggingLevel.Info);
            return;
        }

        if (!TryClaimPid(dedupe, pid, effectivePnl, out var upgraded))
        {
            log($"P&L skip: already posted pid={pid}", StrategyLoggingLevel.Info);
            return;
        }

        log(
            $"P&L report pid={pid} pnl={effectivePnl:F2} {symbol}{(upgraded ? " (upgrade)" : "")}",
            StrategyLoggingLevel.Trading);

        var coreUri = new Uri(coreBaseUrl.TrimEnd('/') + "/");
        var client = new CoreAckClient(http);
        var closedAt = DateTime.UtcNow.ToString("O");

        var ackBody = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["type"] = "ack",
            ["ack_type"] = "position_closed",
            ["positionId"] = pid,
            ["pid"] = pid,
            ["symbol"] = symbol,
            ["side"] = side,
            ["pnl"] = effectivePnl,
            ["grossProfit"] = grossPnl,
            ["currency"] = "",
            ["closedAt"] = closedAt,
            ["accountId"] = bridgeInstanceId,
            ["bridgeInstanceId"] = bridgeInstanceId,
            ["platform"] = "quantower"
        };
        if (!string.IsNullOrWhiteSpace(hubCommandId))
        {
            ackBody["commandId"] = hubCommandId;
            ackBody["signalId"] = hubCommandId;
        }

        var execBody = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["platform"] = "quantower",
            ["symbol"] = symbol,
            ["side"] = side,
            ["eventType"] = "closed",
            ["volume"] = quantity,
            ["entryPrice"] = openPrice,
            ["exitPrice"] = exitPrice,
            ["realizedPnl"] = effectivePnl,
            ["netProfit"] = effectivePnl,
            ["pnl"] = effectivePnl,
            ["grossProfit"] = grossPnl,
            ["pid"] = pid,
            ["positionId"] = pid,
            ["ticket"] = pid,
            ["openedAt"] = openTime.ToString("O"),
            ["closedAt"] = closedAt,
            ["accountId"] = bridgeInstanceId
        };

        var wsClosed = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["type"] = "position_closed",
            ["positionId"] = pid,
            ["pid"] = pid,
            ["symbol"] = symbol,
            ["side"] = side,
            ["pnl"] = effectivePnl,
            ["netProfit"] = effectivePnl,
            ["grossProfit"] = grossPnl,
            ["closedAt"] = closedAt,
            ["accountId"] = bridgeInstanceId
        };

        var ackJson = JsonSerializer.Serialize(ackBody);
        var wsJson = JsonSerializer.Serialize(wsClosed);
        if (!HubPnlMirror.TrySend(wsJson))
            HubPnlMirror.TrySend(ackJson);

        var ackOk = false;
        var execOk = false;

        try
        {
            await client.PostPositionClosedAckAsync(coreUri, ackBody, authToken, ct).ConfigureAwait(false);
            ackOk = true;
        }
        catch (Exception ex)
        {
            log($"P&L /ack post failed pid={pid}: {ex.Message}", StrategyLoggingLevel.Error);
        }

        try
        {
            await client.PostClosedTradeAsync(coreUri, bridgeInstanceId, execBody, authToken, ct)
                .ConfigureAwait(false);
            execOk = true;
        }
        catch (Exception ex)
        {
            log($"P&L /cbot/execution post failed pid={pid}: {ex.Message}", StrategyLoggingLevel.Warning);
        }

        if (ackOk || execOk)
        {
            log($"P&L posted OK pid={pid} pnl={effectivePnl:F2} ack={ackOk} exec={execOk}", StrategyLoggingLevel.Info);
            return;
        }

        dedupe.TryRemove(pid, out _);
        log($"P&L post failed pid={pid}: both /ack and /cbot/execution rejected", StrategyLoggingLevel.Error);
    }

    private static Task PostBodiesWithTradeRetryAsync(
        Trade trade,
        string pid,
        double net,
        double gross,
        string bridgeInstanceId,
        string coreBaseUrl,
        string authToken,
        HttpClient http,
        ConcurrentDictionary<string, double> dedupe,
        Action<string, StrategyLoggingLevel> log,
        CancellationToken ct) =>
        Task.Run(async () =>
        {
            await Task.Delay(500, ct).ConfigureAwait(false);

            var retryNet = net;
            try
            {
                var v = trade.NetPnl?.Value ?? 0;
                if (Math.Abs(v) >= PnlEpsilon) retryNet = v;
            }
            catch
            {
                /* ignore */
            }

            if (Math.Abs(retryNet) < PnlEpsilon)
            {
                try
                {
                    var g = trade.GrossPnl?.Value ?? 0;
                    if (Math.Abs(g) >= PnlEpsilon) retryNet = g;
                }
                catch
                {
                    /* ignore */
                }
            }

            await PostBodiesAsync(
                pid,
                trade.Symbol?.Name ?? "",
                trade.Side == Side.Buy ? "buy" : "sell",
                trade.Quantity,
                0,
                trade.Price,
                retryNet,
                gross != 0 ? gross : retryNet,
                trade.DateTime,
                bridgeInstanceId,
                coreBaseUrl,
                authToken,
                http,
                dedupe,
                log,
                PnlPostSource.Trade,
                hubCommandId: null,
                ct).ConfigureAwait(false);
        }, ct);

    /// <summary>Fallback when history API is empty — poll up to ~30s.</summary>
    public static void SchedulePollAfterBridgeClose(
        Symbol sym,
        Account acc,
        string bridgeInstanceId,
        string coreBaseUrl,
        string authToken,
        HttpClient http,
        ConcurrentDictionary<string, double> dedupe,
        Action<string, StrategyLoggingLevel> log)
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    await Task.Delay(attempt == 0 ? 300 : 1500, CancellationToken.None).ConfigureAwait(false);

                    var closed = Core.Instance.ClosedPositions?
                        .Where(c => MatchesBinding(c, sym, acc) || SymbolNamesMatch(c.Symbol?.Name, sym.Name))
                        .OrderByDescending(c => c.OpenTime)
                        .FirstOrDefault();

                    if (closed != null)
                    {
                        await ReportAsync(closed, bridgeInstanceId, coreBaseUrl, authToken, http, dedupe, log)
                            .ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    log($"P&L poll error: {ex.Message}", StrategyLoggingLevel.Error);
                    return;
                }
            }

            log($"P&L poll: no closed row in history for {sym.Name} (snapshot/trade path should have posted)", StrategyLoggingLevel.Info);
        });
    }

    private static bool SymbolNamesMatch(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool AccountsMatch(Account cpAcc, Account strategyAcc)
    {
        if (string.Equals(cpAcc.Id, strategyAcc.Id, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(cpAcc.Name, strategyAcc.Name, StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrEmpty(cpAcc.ConnectionId) &&
               cpAcc.ConnectionId == strategyAcc.ConnectionId;
    }

    private static string ResolvePid(ClosedPosition cp)
    {
        var id = cp.Id?.Trim();
        if (!string.IsNullOrEmpty(id)) return id;
        return cp.UniqueId?.Trim() ?? "";
    }

    internal static double ReadNetPnlForRisk(ClosedPosition cp) => ReadNetPnl(cp);

    private static double ReadNetPnl(ClosedPosition cp)
    {
        try
        {
            var net = cp.NetPnL;
            if (net != null && double.IsFinite(net.Value)) return net.Value;
        }
        catch
        {
            /* ignore */
        }

        return 0;
    }

    private static double ReadGrossPnl(ClosedPosition cp, double netFallback)
    {
        try
        {
            var gross = cp.GrossPnL;
            if (gross != null && double.IsFinite(gross.Value)) return gross.Value;
        }
        catch
        {
            /* ignore */
        }

        return netFallback;
    }
}
