using CLIGoalHelper.Cache;

namespace CLIGoalHelper.Metrics;

public sealed record BugMetricsSnapshot(
    int Total,
    int Production,
    int Test,
    int Dev,
    int Unknown,
    double Dlr90Percentage,
    double Dlr90TestingPercentage,
    IReadOnlyList<MonthDlrBucket> Monthly);

public sealed record MonthDlrBucket(string Label, int Total, int Production, int Test)
{
    public double ProductionPercentage => Total == 0 ? 0 : 100.0 * Production / Total;
    public double TestingPercentage => Total == 0 ? 0 : 100.0 * (Production + Test) / Total;
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
        var dlrTesting = current.Total == 0 ? 0 : 100.0 * (current.Production + current.Test) / current.Total;

        return new BugMetricsSnapshot(
            Total: current.Total,
            Production: current.Production,
            Test: current.Test,
            Dev: current.Dev,
            Unknown: current.Unknown,
            Dlr90Percentage: dlr,
            Dlr90TestingPercentage: dlrTesting,
            Monthly: ComputeRollingMonthlySnapshots(now));
    }

    /// <summary>
    /// For each month from the goal start to the current month, evaluates the trailing-90d
    /// DLR as of the month's end (or today, for the in-progress month). Sync ensures the
    /// cache covers the required lookback for the earliest snapshot.
    /// </summary>
    private List<MonthDlrBucket> ComputeRollingMonthlySnapshots(DateTimeOffset now)
    {
        var results = new List<MonthDlrBucket>();
        var firstOfThisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var firstOfGoalMonth = new DateTime(_goalStartDate.Year, _goalStartDate.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthCount = (firstOfThisMonth.Year - firstOfGoalMonth.Year) * 12
            + (firstOfThisMonth.Month - firstOfGoalMonth.Month) + 1;
        if (monthCount < 1) monthCount = 1;

        for (var i = monthCount - 1; i >= 0; i--)
        {
            var monthStart = firstOfThisMonth.AddMonths(-i);
            var monthEndExclusive = monthStart.AddMonths(1);
            // For in-progress month, anchor the window at "now"; otherwise at end-of-month.
            var anchor = new DateTimeOffset(monthEndExclusive, TimeSpan.Zero) > now
                ? now
                : new DateTimeOffset(monthEndExclusive.AddTicks(-1), TimeSpan.Zero);

            var windowStart = anchor.AddDays(-_trailingDays);
            var counts = _cache.Bugs.CountInWindow(windowStart, anchor);
            results.Add(new MonthDlrBucket(
                Label: monthStart.ToString("yyyy-MM"),
                Total: counts.Total,
                Production: counts.Production,
                Test: counts.Test));
        }
        return results;
    }
}
