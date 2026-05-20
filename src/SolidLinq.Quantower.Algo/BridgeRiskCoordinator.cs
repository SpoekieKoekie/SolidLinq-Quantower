using System;

using System.Linq;

using System.Reflection;

using TradingPlatform.BusinessLayer;



namespace SolidLinq.Quantower.Algo;



/// <summary>cBot-style daily/weekly and overall loss/profit limits (see WebSocketBridgeBot risk poll).</summary>

internal sealed class BridgeRiskCoordinator

{

    private readonly object _lock = new();



    private Account? _account;



    public double DailyLossLimit { get; set; }

    public double WeeklyLossLimit { get; set; }

    public double DailyProfitTarget { get; set; }

    public double WeeklyProfitTarget { get; set; }

    public bool StopTakingNewTradesWhenHit { get; set; } = true;

    public bool CloseOpenPositionsWhenHit { get; set; } = true;

    public bool EnableOverallMaxLoss { get; set; }

    public double OverallMaxLoss { get; set; }

    public bool EnableOverallMaxProfit { get; set; }

    public double OverallMaxProfit { get; set; }

    public bool UseEquityForDrawdown { get; set; } = true;

    public BridgeDrawdownMode DrawdownMode { get; set; } = BridgeDrawdownMode.Static;

    public bool ManualUnlockRequiredForOverallHit { get; set; } = true;



    private double _dailyRealized;

    private double _weeklyRealized;

    private DateTime _dailyAnchor = DateTime.MinValue;

    private DateTime _weeklyAnchor = DateTime.MinValue;

    private DateTime _eodAnchor = DateTime.MinValue;



    private double _startingBalance;

    private double _startingEquity;

    private double _peakBalance;

    private double _peakEquity;

    private double _eodBalance;

    private double _eodEquity;



    private bool _started;

    private bool _overallLockTriggered;



    public bool TradingLocked { get; private set; }

    public string TradingLockReason { get; private set; } = "";



    public void Bind(Account? account, Symbol? _)

    {

        lock (_lock)

        {

            _account = account;

        }

    }



    public void EnsureStarted(DateTime nowUtc)

    {

        lock (_lock)

        {

            if (_started || _account == null) return;



            var balance = SafeBalance(_account);

            var equity = SafeEquity(_account, balance);

            _startingBalance = balance;

            _startingEquity = equity;

            _peakBalance = balance;

            _peakEquity = equity;

            _eodBalance = balance;

            _eodEquity = equity;



            _dailyAnchor = nowUtc.Date;

            _weeklyAnchor = WeekStartUtc(nowUtc);

            _eodAnchor = nowUtc.Date;

            _started = true;

        }

    }



    public void NoteClosedRealized(double netPnl, DateTime nowUtc)

    {

        lock (_lock)

        {

            RolloverIfNeeded(nowUtc);

            _dailyRealized += netPnl;

            _weeklyRealized += netPnl;

        }

    }



    public bool TryEnterNewOrder(out string reason)

    {

        lock (_lock)

        {

            reason = "";

            if (TradingLocked && StopTakingNewTradesWhenHit)

            {

                reason = string.IsNullOrEmpty(TradingLockReason) ? "Trading locked" : TradingLockReason;

                return false;

            }

            return true;

        }

    }



    public void Poll(Action<string, StrategyLoggingLevel> log)

    {

        Account? acc;

        lock (_lock)

        {

            acc = _account;

        }



        if (acc == null) return;



        var now = DateTime.UtcNow;

        double openPnl;

        try

        {

            openPnl = CurrentOpenProfit(acc);

        }

        catch

        {

            return;

        }



        bool started;

        double dailyTotal;

        double weeklyTotal;

        double metric;

        lock (_lock)

        {

            started = _started;

            if (!started) return;



            UpdatePeaksAndEod(acc, now);

            RolloverIfNeeded(now);



            dailyTotal = _dailyRealized + openPnl;

            weeklyTotal = _weeklyRealized + openPnl;

            metric = CurrentMetric(acc);

        }



        var hit = false;

        var isOverallHit = false;

        var hitReason = "";



        if (DailyLossLimit > 0 && dailyTotal <= -Math.Abs(DailyLossLimit))

            hitReason = "Daily loss limit hit";

        else if (WeeklyLossLimit > 0 && weeklyTotal <= -Math.Abs(WeeklyLossLimit))

            hitReason = "Weekly loss limit hit";

        else if (DailyProfitTarget > 0 && dailyTotal >= Math.Abs(DailyProfitTarget))

            hitReason = "Daily profit target hit";

        else if (WeeklyProfitTarget > 0 && weeklyTotal >= Math.Abs(WeeklyProfitTarget))

            hitReason = "Weekly profit target hit";



        if (!string.IsNullOrEmpty(hitReason))

            hit = true;



        if (!hit && EnableOverallMaxLoss && OverallMaxLoss > 0)

        {

            double floor;

            lock (_lock)

            {

                floor = CalculateOverallLossFloor(acc);

            }



            if (metric <= floor)

            {

                hit = true;

                isOverallHit = true;

                hitReason = $"Overall max loss hit ({DrawdownMode}, metric={metric:F2}, floor={floor:F2})";

            }

        }



        if (!hit && EnableOverallMaxProfit && OverallMaxProfit > 0)

        {

            double profitFromStart;

            lock (_lock)

            {

                profitFromStart = metric - CurrentStartMetric();

            }



            if (profitFromStart >= OverallMaxProfit)

            {

                hit = true;

                isOverallHit = true;

                hitReason = $"Overall max profit hit (profit={profitFromStart:F2})";

            }

        }



        if (!hit) return;



        var shouldLog = false;

        var shouldClose = false;

        lock (_lock)

        {

            if (!TradingLocked || (isOverallHit && !_overallLockTriggered))

            {

                TradingLocked = true;

                TradingLockReason = hitReason;

                if (isOverallHit)

                    _overallLockTriggered = true;

                shouldLog = true;

                shouldClose = CloseOpenPositionsWhenHit;

            }

        }



        if (shouldLog)

            log($"Trading locked: {hitReason}", StrategyLoggingLevel.Trading);

        if (shouldClose)

            CloseAllForAccount(acc, hitReason, log);

    }



    private void UpdatePeaksAndEod(Account acc, DateTime nowUtc)

    {

        var balance = SafeBalance(acc);

        var equity = SafeEquity(acc, balance);

        if (balance > _peakBalance) _peakBalance = balance;

        if (equity > _peakEquity) _peakEquity = equity;



        var day = nowUtc.Date;

        if (_eodAnchor == DateTime.MinValue) _eodAnchor = day;

        if (day > _eodAnchor)

        {

            _eodAnchor = day;

            _eodBalance = balance;

            _eodEquity = equity;

        }

    }



    private void RolloverIfNeeded(DateTime nowUtc)

    {

        var day = nowUtc.Date;

        var week = WeekStartUtc(nowUtc);

        if (_dailyAnchor == DateTime.MinValue) _dailyAnchor = day;

        if (_weeklyAnchor == DateTime.MinValue) _weeklyAnchor = week;



        if (day > _dailyAnchor)

        {

            _dailyAnchor = day;

            _dailyRealized = 0;

            if (!(_overallLockTriggered && ManualUnlockRequiredForOverallHit))

            {

                TradingLocked = false;

                TradingLockReason = "";

            }

        }



        if (week > _weeklyAnchor)

        {

            _weeklyAnchor = week;

            _weeklyRealized = 0;

            if (!(_overallLockTriggered && ManualUnlockRequiredForOverallHit))

            {

                TradingLocked = false;

                TradingLockReason = "";

            }

        }

    }



    private double CalculateOverallLossFloor(Account acc)

    {

        if (OverallMaxLoss <= 0) return double.MinValue;



        return DrawdownMode switch

        {

            BridgeDrawdownMode.Trailing => CurrentPeakMetric() - OverallMaxLoss,

            BridgeDrawdownMode.EOD => CurrentEodMetric() - OverallMaxLoss,

            _ => CurrentStartMetric() - OverallMaxLoss

        };

    }



    private double CurrentMetric(Account acc)

    {

        var balance = SafeBalance(acc);

        return UseEquityForDrawdown ? SafeEquity(acc, balance) : balance;

    }



    private double CurrentStartMetric() =>

        UseEquityForDrawdown ? _startingEquity : _startingBalance;



    private double CurrentPeakMetric() =>

        UseEquityForDrawdown ? _peakEquity : _peakBalance;



    private double CurrentEodMetric() =>

        UseEquityForDrawdown ? _eodEquity : _eodBalance;



    private static double SafeBalance(Account a)

    {

        try

        {

            return a.Balance;

        }

        catch

        {

            return 0;

        }

    }



    private static double SafeEquity(Account a, double balanceFallback)

    {

        try

        {

            var prop = a.GetType().GetProperty("Equity", BindingFlags.Public | BindingFlags.Instance);

            if (prop?.GetValue(a) is double equity && double.IsFinite(equity))

                return equity;

        }

        catch

        {

            /* ignore */

        }



        try

        {

            return balanceFallback + CurrentOpenProfit(a);

        }

        catch

        {

            return balanceFallback;

        }

    }



    private static double CurrentOpenProfit(Account acc)

    {

        try

        {

            return Core.Instance.Positions.Where(p => p.Account == acc).Sum(p => p.GrossPnL?.Value ?? 0);

        }

        catch

        {

            return 0;

        }

    }



    private static void CloseAllForAccount(Account acc, string reason,

        Action<string, StrategyLoggingLevel> log)

    {

        try

        {

            log($"Risk limit hit - closing all positions. Reason: {reason}", StrategyLoggingLevel.Trading);

            foreach (var p in Core.Instance.Positions.Where(p => p.Account == acc).ToArray())

            {

                var res = p.Close();

                if (res.Status == TradingOperationResultStatus.Failure)

                    log($"Close failed: {res.Message}", StrategyLoggingLevel.Error);

            }

        }

        catch (Exception ex)

        {

            log($"Risk close-all error: {ex.Message}", StrategyLoggingLevel.Error);

        }

    }



    private static DateTime WeekStartUtc(DateTime t)

    {

        var d = t.Date;

        var dow = (int)d.DayOfWeek;

        var offset = dow == 0 ? -6 : 1 - dow;

        return d.AddDays(offset);

    }

}


