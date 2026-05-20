using System;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace SolidLinq.Quantower.Algo;

/// <summary>Cancels SL/TP bracket orders left open after a position close (Quantower does not always OCO-cancel them).</summary>
internal static class ProtectiveOrderCleanup
{
    public static void CancelBracketsForPosition(Position position, Action<string, StrategyLoggingLevel>? log)
    {
        if (position.Symbol == null || position.Account == null) return;

        TryCancelOrder(position.StopLoss, "SL", log);
        TryCancelOrder(position.TakeProfit, "TP", log);

        var pid = position.Id;
        if (string.IsNullOrWhiteSpace(pid)) return;

        var symName = position.Symbol.Name;
        var acc = position.Account;
        foreach (var order in Core.Instance.Orders.ToArray())
        {
            if (!OrderMatchesBinding(order, symName, acc)) continue;
            if (!string.Equals(order.PositionId, pid, StringComparison.Ordinal)) continue;
            if (!IsCancellable(order)) continue;
            TryCancelOrder(order, "linked", log);
        }
    }

    /// <summary>After exit, cancel working protective orders on symbol/account (orphaned brackets).</summary>
    public static void CancelOrphanedForSymbol(Symbol sym, Account acc, Action<string, StrategyLoggingLevel>? log)
    {
        var symName = sym.Name ?? "";
        foreach (var order in Core.Instance.Orders.ToArray())
        {
            if (!OrderMatchesBinding(order, symName, acc)) continue;
            if (!IsCancellable(order)) continue;
            if (!IsProtectiveOrder(order)) continue;

            var pid = order.PositionId;
            if (!string.IsNullOrWhiteSpace(pid) &&
                Core.Instance.Positions.Any(p =>
                    PositionMatchesBinding(p, symName, acc) &&
                    string.Equals(p.Id, pid, StringComparison.Ordinal)))
                continue;

            TryCancelOrder(order, "orphan", log);
        }
    }

    private static bool OrderMatchesBinding(Order order, string symName, Account acc)
    {
        if (order.Symbol == null || order.Account == null) return false;
        return SymbolNamesMatch(order.Symbol.Name, symName) && AccountsMatch(order.Account, acc);
    }

    private static bool PositionMatchesBinding(Position p, string symName, Account acc)
    {
        if (p.Symbol == null || p.Account == null) return false;
        return SymbolNamesMatch(p.Symbol.Name, symName) && AccountsMatch(p.Account, acc);
    }

    private static bool SymbolNamesMatch(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool AccountsMatch(Account a, Account b)
    {
        if (string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrEmpty(a.ConnectionId) && a.ConnectionId == b.ConnectionId;
    }

    private static bool IsProtectiveOrder(Order order)
    {
        var behavior = order.OrderType?.Behavior;
        return behavior is OrderTypeBehavior.Stop or OrderTypeBehavior.StopLimit
            or OrderTypeBehavior.TrailingStop or OrderTypeBehavior.Limit;
    }

    private static bool IsCancellable(Order order) =>
        order.Status is OrderStatus.Opened or OrderStatus.PartiallyFilled or OrderStatus.Inactive;

    private static void TryCancelOrder(Order? order, string label, Action<string, StrategyLoggingLevel>? log)
    {
        if (order == null || !IsCancellable(order)) return;

        try
        {
            var res = Core.Instance.CancelOrder(order);
            if (res.Status == TradingOperationResultStatus.Failure)
            {
                var alt = order.Cancel();
                if (alt.Status == TradingOperationResultStatus.Failure)
                    log?.Invoke($"Cancel {label} failed: {res.Message ?? alt.Message}", StrategyLoggingLevel.Error);
                else
                    log?.Invoke($"Cancelled {label} order ({order.OrderType?.Name})", StrategyLoggingLevel.Info);
            }
            else
                log?.Invoke($"Cancelled {label} order ({order.OrderType?.Name})", StrategyLoggingLevel.Info);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Cancel {label} error: {ex.Message}", StrategyLoggingLevel.Error);
        }
    }
}
