using System;
using TradingPlatform.BusinessLayer;

namespace SolidLinq.Quantower.Algo;

internal static class SymbolLotNormalizer
{
    /// <summary>Rounds lot size up to symbol <see cref="Symbol.LotStep"/> and clamps to min/max.</summary>
    public static double NormalizeLotsUp(Symbol sym, double lots)
    {
        if (!double.IsFinite(lots) || lots <= 0) return 0;

        var step = sym.LotStep;
        if (!double.IsFinite(step) || step <= 0)
            step = sym.MinLot > 0 ? sym.MinLot : 0.01;

        var min = sym.MinLot > 0 ? sym.MinLot : step;
        var max = sym.MaxLot > 0 ? sym.MaxLot : double.MaxValue;

        var n = Math.Ceiling(lots / step) * step;
        if (n < min) n = min;
        if (n > max) n = max;

        var decimals = step >= 1 ? 0 : Math.Max(0, (int)Math.Ceiling(-Math.Log10(step)));
        return Math.Round(n, decimals, MidpointRounding.AwayFromZero);
    }
}
