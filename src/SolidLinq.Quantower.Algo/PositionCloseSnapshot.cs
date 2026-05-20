using System;
using TradingPlatform.BusinessLayer;

namespace SolidLinq.Quantower.Algo;

/// <summary>Captured before flattening; Quantower often has no <see cref="Core.ClosedPositions"/> row for algo strategies.</summary>
internal sealed class PositionCloseSnapshot
{
    public string Pid { get; init; } = "";
    public string SymbolName { get; init; } = "";
    public string Side { get; init; } = "";
    public double Quantity { get; init; }
    public double OpenPrice { get; init; }
    public double ExitPrice { get; init; }
    public double NetPnl { get; init; }
    public double GrossPnl { get; init; }
    public DateTime OpenTime { get; init; }
    public string HubCommandId { get; init; } = "";

    public static PositionCloseSnapshot? TryCapture(Position p, Symbol sym, string? hubCommandId = null)
    {
        if (sym == null) return null;

        var pid = (p.Id ?? "").Trim();
        if (pid.Length == 0) pid = (p.UniqueId ?? "").Trim();
        if (pid.Length == 0) pid = Guid.NewGuid().ToString("N");

        double net = 0, gross = 0, exit = 0;
        try
        {
            net = p.NetPnL?.Value ?? 0;
        }
        catch
        {
            /* ignore */
        }

        try
        {
            gross = p.GrossPnL?.Value ?? net;
        }
        catch
        {
            gross = net;
        }

        try
        {
            exit = p.CurrentPrice;
        }
        catch
        {
            /* ignore */
        }

        if (exit <= 0)
        {
            try
            {
                exit = p.Side == TradingPlatform.BusinessLayer.Side.Buy ? sym.Bid : sym.Ask;
            }
            catch
            {
                exit = 0;
            }
        }

        return new PositionCloseSnapshot
        {
            Pid = pid,
            SymbolName = sym.Name ?? "",
            Side = p.Side == TradingPlatform.BusinessLayer.Side.Buy ? "buy" : "sell",
            Quantity = p.Quantity,
            OpenPrice = p.OpenPrice,
            ExitPrice = exit,
            NetPnl = net,
            GrossPnl = gross,
            OpenTime = p.OpenTime,
            HubCommandId = hubCommandId ?? ""
        };
    }
}
