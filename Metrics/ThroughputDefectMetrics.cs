using CLIGoalHelper.Cache;

namespace CLIGoalHelper.Metrics;

public sealed record ThroughputDefectMonth(string YearMonth, int Prs, int Bugs)
{
    // Indus defects per completed (non-Prototype) PR — a trend indicator, not an absolute
    // defect density (the bug and PR scopes differ). Null when there were no PRs that month.
    public double? BugsPerPr => Prs == 0 ? null : (double)Bugs / Prs;
}

/// <summary>
/// Pairs PR throughput against defect volume per calendar month, to see whether a rising
/// PR throughput coincides with a rising bugs-per-PR rate. PRs are completed PRs across all
/// repos except the excluded (Prototype) ones; bugs are the boards-project bugs in the cache.
/// </summary>
public sealed class ThroughputDefectMetrics
{
    private readonly CacheStore _cache;
    private readonly DateTime _goalStartDate;
    private readonly IReadOnlyCollection<string> _excludedRepoIds;

    public ThroughputDefectMetrics(
        CacheStore cache,
        DateTime goalStartDate,
        IReadOnlyCollection<string> excludedRepoIds)
    {
        _cache = cache;
        _goalStartDate = goalStartDate;
        _excludedRepoIds = excludedRepoIds;
    }

    public IReadOnlyList<ThroughputDefectMonth> Compute()
    {
        var now = DateTimeOffset.UtcNow;
        var since = new DateTimeOffset(
            new DateTime(_goalStartDate.Year, _goalStartDate.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.Zero);

        var prByMonth = _cache.PullRequests.MonthlyBuckets(since, _excludedRepoIds)
            .ToDictionary(m => m.YearMonth, m => m.Total);
        var bugByMonth = _cache.Bugs.MonthlyCounts(since)
            .ToDictionary(m => m.YearMonth, m => m.Count);

        // Walk every month from goal start to now so months with PRs-but-no-bugs (or vice versa)
        // still appear with a zero rather than being dropped by either GROUP BY.
        var results = new List<ThroughputDefectMonth>();
        var firstMonth = new DateTime(since.Year, since.Month, 1);
        var lastMonth = new DateTime(now.Year, now.Month, 1);
        for (var month = firstMonth; month <= lastMonth; month = month.AddMonths(1))
        {
            var key = month.ToString("yyyy-MM");
            results.Add(new ThroughputDefectMonth(
                YearMonth: key,
                Prs: prByMonth.GetValueOrDefault(key),
                Bugs: bugByMonth.GetValueOrDefault(key)));
        }
        return results;
    }
}
