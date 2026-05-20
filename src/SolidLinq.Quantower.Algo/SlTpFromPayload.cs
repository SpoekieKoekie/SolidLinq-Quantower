using SolidLinq.Quantower.Models;
using TradingPlatform.BusinessLayer;

namespace SolidLinq.Quantower.Algo;

internal static class SlTpFromPayload
{
    internal static void Apply(
        PlaceOrderRequestParameters req,
        HubDispatchMessage cmd,
        Symbol sym,
        Side side,
        double bid,
        double ask,
        BridgeSlMode slMode,
        bool slInPayload,
        bool tpInPayload,
        double slMul,
        double tpMul)
    {
        var allowSl = slMode != BridgeSlMode.DisableSl;
        var slMult = double.IsFinite(slMul) && slMul > 0 ? slMul : 1.0;
        var tpMult = double.IsFinite(tpMul) && tpMul > 0 ? tpMul : 1.0;

        var slPct = NormalizePercent(PickPercent(cmd.SlPct, cmd.SlPercent));
        var tpPct = NormalizePercent(PickPercent(cmd.TpPct, cmd.TpPercent));
        if (slPct.HasValue) slPct *= slMult;
        if (tpPct.HasValue) tpPct *= tpMult;

        var entry = side == Side.Buy ? (ask > 0 ? ask : bid) : (bid > 0 ? bid : ask);
        if (entry <= 0) return;

        var tick = sym.TickSize;
        if (!double.IsFinite(tick) || tick <= 0) tick = 0;

        double slPrice = 0, tpPrice = 0;
        if (allowSl && slInPayload && slPct is > 0)
        {
            if (side == Side.Buy)
                slPrice = entry * (1.0 - slPct.Value / 100.0);
            else
                slPrice = entry * (1.0 + slPct.Value / 100.0);
        }

        if (tpInPayload && tpPct is > 0)
        {
            if (side == Side.Buy)
                tpPrice = entry * (1.0 + tpPct.Value / 100.0);
            else
                tpPrice = entry * (1.0 - tpPct.Value / 100.0);
        }

        if (tick > 0)
        {
            if (slPrice > 0)
            {
                var slOff = side == Side.Buy
                    ? (entry - slPrice) / tick
                    : (slPrice - entry) / tick;
                if (slOff > 0)
                    req.StopLoss = SlTpHolder.CreateSL(slOff, PriceMeasurement.Offset);
            }

            if (tpPrice > 0)
            {
                var tpOff = side == Side.Buy
                    ? (tpPrice - entry) / tick
                    : (entry - tpPrice) / tick;
                if (tpOff > 0)
                    req.TakeProfit = SlTpHolder.CreateTP(tpOff, PriceMeasurement.Offset);
            }
        }
        else
        {
            if (slPrice > 0)
                req.StopLoss = SlTpHolder.CreateSL(slPrice, PriceMeasurement.Absolute);
            if (tpPrice > 0)
                req.TakeProfit = SlTpHolder.CreateTP(tpPrice, PriceMeasurement.Absolute);
        }
    }

    private static double? PickPercent(double? a, double? b)
    {
        if (a is > 0 && double.IsFinite(a.Value)) return a;
        if (b is > 0 && double.IsFinite(b.Value)) return b;
        if (a != null && double.IsFinite(a.Value) && a.Value != 0) return Math.Abs(a.Value);
        if (b != null && double.IsFinite(b.Value) && b.Value != 0) return Math.Abs(b.Value);
        return null;
    }

    /// <summary>TV may send 2 for 2% or occasionally 0.02.</summary>
    private static double? NormalizePercent(double? pct)
    {
        if (pct is not > 0 || !double.IsFinite(pct.Value)) return null;
        var v = pct.Value;
        if (v > 0 && v < 0.1) return v * 100.0;
        return v;
    }
}
