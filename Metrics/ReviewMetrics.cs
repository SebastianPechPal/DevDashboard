using CLIGoalHelper.Cache;

namespace CLIGoalHelper.Metrics;

public sealed record ReviewMetricsSnapshot(
    int TrailingTotal,
    int TrailingPass,
    int TrailingFail,
    int TrailingNoVote,
    double TrailingPassPercentage,
    double? MedianBusinessHours,
    IReadOnlyList<MonthBucket> Monthly,
    int PersonalFirstVotes30d);

public sealed record MonthBucket(string YearMonth, int Total, int Pass)
{
    public double PassPercentage => Total == 0 ? 0 : 100.0 * Pass / Total;
}

public sealed class ReviewMetrics
{
    private readonly CacheStore _cache;
    private readonly int _trailingDays;
    private readonly DateTime _goalStartDate;
    private readonly string? _personalIdentityId;
    private readonly IReadOnlyCollection<string> _excludedRepoIds;

    public ReviewMetrics(
        CacheStore cache,
        int trailingDays,
        DateTime goalStartDate,
        string? personalIdentityId,
        IReadOnlyCollection<string> excludedRepoIds)
    {
        _cache = cache;
        _trailingDays = trailingDays;
        _goalStartDate = goalStartDate;
        _personalIdentityId = personalIdentityId;
        _excludedRepoIds = excludedRepoIds;
    }

    public ReviewMetricsSnapshot Compute()
    {
        var now = DateTimeOffset.UtcNow;
        var trailingSince = now.AddDays(-_trailingDays);
        var trendSince = new DateTimeOffset(
            new DateTime(_goalStartDate.Year, _goalStartDate.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.Zero);
        var personalSince = now.AddDays(-30);

        var trailing = _cache.PullRequests.TrailingWindow(trailingSince, _excludedRepoIds);
        var monthly = _cache.PullRequests.MonthlyBuckets(trendSince, _excludedRepoIds);
        var median = _cache.PullRequests.MedianBusinessHours(trailingSince, _excludedRepoIds);
        var personalVotes = _personalIdentityId != null
            ? _cache.PullRequests.CountFirstVotesBy(_personalIdentityId, personalSince, _excludedRepoIds)
            : 0;

        var passPct = trailing.Total == 0 ? 0 : 100.0 * trailing.Pass / trailing.Total;

        return new ReviewMetricsSnapshot(
            TrailingTotal: trailing.Total,
            TrailingPass: trailing.Pass,
            TrailingFail: trailing.Fail,
            TrailingNoVote: trailing.NoVote,
            TrailingPassPercentage: passPct,
            MedianBusinessHours: median,
            Monthly: monthly.Select(m => new MonthBucket(m.YearMonth, m.Total, m.Pass)).ToList(),
            PersonalFirstVotes30d: personalVotes);
    }
}
