using CLIGoalHelper.Cache;

namespace CLIGoalHelper.Metrics;

public sealed record BugMetricsSnapshot(
    int Total,
    int Production,
    int QaTest,
    int Dev,
    int Unknown,
    double Dlr90Percentage,
    IReadOnlyList<MonthProductionDlr> Monthly);

public sealed record MonthProductionDlr(string Label, int Total, int Production)
{
    public double ProductionPercentage => Total == 0 ? 0 : 100.0 * Production / Total;
}

public sealed class BugMetrics
{
    private readonly CacheStore _cache;
    private readonly int _trailingDays;
    private readonly DateTime _goalStartDate;

    public BugMetrics(CacheStore cache, int trailingDays, DateTime goalStartDate)
    {
        _cache = cache;
        _trailingDays = trailingDays;
        _goalStartDate = goalStartDate;
    }

    public BugMetricsSnapshot Compute()
    {
        var now = DateTimeOffset.UtcNow;
        var trailingSince = now.AddDays(-_trailingDays);

        var current = _cache.Bugs.CountInWindow(trailingSince, now);
        var dlr = current.Total == 0 ? 0 : 100.0 * current.Production / current.Total;

        return new BugMetricsSnapshot(
            Total: current.Total,
            Production: current.Production,
            QaTest: current.QaTest,
            Dev: current.Dev,
            Unknown: current.Unknown,
            Dlr90Percentage: dlr,
            Monthly: ComputeMonthlyProductionDlr(now));
    }

    /// <summary>
    /// For each month from the goal start to the current month, the trailing-90d DLR90
    /// Production leakage as of that month's end (or now, for the in-progress month).
    /// </summary>
    private List<MonthProductionDlr> ComputeMonthlyProductionDlr(DateTimeOffset now)
    {
        var results = new List<MonthProductionDlr>();
        var firstOfThisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var firstOfGoalMonth = new DateTime(_goalStartDate.Year, _goalStartDate.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthCount = (firstOfThisMonth.Year - firstOfGoalMonth.Year) * 12
            + (firstOfThisMonth.Month - firstOfGoalMonth.Month) + 1;
        if (monthCount < 1) monthCount = 1;

        for (var i = monthCount - 1; i >= 0; i--)
        {
            var monthStart = firstOfThisMonth.AddMonths(-i);
            var monthEndExclusive = monthStart.AddMonths(1);
            // For the in-progress month, anchor the window at "now"; otherwise at end-of-month.
            var anchor = new DateTimeOffset(monthEndExclusive, TimeSpan.Zero) > now
                ? now
                : new DateTimeOffset(monthEndExclusive.AddTicks(-1), TimeSpan.Zero);

            var windowStart = anchor.AddDays(-_trailingDays);
            var counts = _cache.Bugs.CountInWindow(windowStart, anchor);
            results.Add(new MonthProductionDlr(
                Label: monthStart.ToString("yyyy-MM"),
                Total: counts.Total,
                Production: counts.Production));
        }
        return results;
    }
}
