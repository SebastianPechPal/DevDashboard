using CLIGoalHelper.Ado;
using CLIGoalHelper.BusinessTime;
using CLIGoalHelper.Cache;
using Spectre.Console;

namespace CLIGoalHelper.Sync;

/// <summary>
/// Orchestrates a sync pass: pulls PR metadata for each repo, then derives the
/// first-required-vote attribution for any PR that doesn't have it cached yet.
/// Bounded concurrency on thread fetches keeps the ADO load reasonable.
/// </summary>
public sealed class SyncService
{
    private const int ThreadFetchConcurrency = 10;

    private readonly CacheStore _cache;
    private readonly PullRequestService _prService;
    private readonly ThreadService _threadService;
    private readonly WorkItemService _workItemService;
    private readonly BusinessClock _clock;
    private readonly HashSet<string> _requiredReviewerIds;
    private readonly double _slaHours;
    private readonly int _backfillDays;
    private readonly int _bugBackfillDays;
    private readonly string _boardsProject;
    private readonly SemaphoreSlim _fetchSemaphore = new(ThreadFetchConcurrency);

    public SyncService(
        CacheStore cache,
        PullRequestService prService,
        ThreadService threadService,
        WorkItemService workItemService,
        BusinessClock clock,
        IEnumerable<string> requiredReviewerIds,
        double slaHours,
        int backfillDays,
        string boardsProject,
        int trailingWindowDays,
        DateTime goalStartDate)
    {
        _cache = cache;
        _prService = prService;
        _threadService = threadService;
        _workItemService = workItemService;
        _clock = clock;
        _requiredReviewerIds = requiredReviewerIds.ToHashSet();
        _slaHours = slaHours;
        _backfillDays = backfillDays;
        _boardsProject = boardsProject;

        // The earliest rolling-90d snapshot is at the start-of-month for GoalStartDate's month.
        // We need cached data from that start minus the 90d lookback, plus a small buffer.
        var goalMonthStart = new DateTime(goalStartDate.Year, goalStartDate.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliestNeeded = goalMonthStart.AddDays(-trailingWindowDays - 7);
        _bugBackfillDays = Math.Max(trailingWindowDays, (int)(DateTime.UtcNow - earliestNeeded).TotalDays);
    }

    public async Task SyncAsync(ProgressContext progress, CancellationToken ct = default)
    {
        foreach (var repo in _cache.Repos.GetAll())
        {
            await SyncRepoAsync(repo, progress, ct);
            _cache.Repos.SetLastSync(repo.Id, DateTimeOffset.UtcNow);
        }
        await SyncBugsAsync(progress, ct);
    }

    private async Task SyncBugsAsync(ProgressContext progress, CancellationToken ct)
    {
        var task = progress.AddTask($"[cyan]Bugs ({_boardsProject})[/]");
        task.MaxValue = 1;

        var since = DateTimeOffset.UtcNow.AddDays(-_bugBackfillDays);
        var bugs = await _workItemService.GetBugsAsync(_boardsProject, since, ct);
        foreach (var bug in bugs)
        {
            _cache.Bugs.Upsert(bug);
        }

        task.Description = $"[cyan]Bugs ({_boardsProject})[/] [grey]({bugs.Count} bugs/issues over {_bugBackfillDays}d)[/]";
        task.Value = task.MaxValue;
    }

    private async Task SyncRepoAsync(CachedRepo repo, ProgressContext progress, CancellationToken ct)
    {
        var task = progress.AddTask($"[cyan]{repo.Name}[/]");
        task.MaxValue = 1; // placeholder until we know how many threads to fetch

        var minTime = repo.LastSyncUtc ?? DateTimeOffset.UtcNow.AddDays(-_backfillDays);

        var active = await _prService.ListAsync(repo.Id, PullRequestStatus.Active, ct: ct);
        var completed = await _prService.ListAsync(repo.Id, PullRequestStatus.Completed, minTime, ct);
        var abandoned = await _prService.ListAsync(repo.Id, PullRequestStatus.Abandoned, minTime, ct);

        foreach (var pr in active.Concat(completed).Concat(abandoned))
        {
            _cache.PullRequests.UpsertCore(pr);
        }

        // Reconcile orphaned actives: cache rows still marked Active that ADO no longer
        // returns as active. Happens when a PR closes between two syncs and its closedDate
        // ends up earlier than the next sync's minTime. Re-fetch each individually.
        var liveActiveIds = active.Select(p => p.Id).ToHashSet();
        var orphaned = _cache.PullRequests.GetActiveForRepo(repo.Id)
            .Where(p => !liveActiveIds.Contains(p.Id))
            .ToList();
        foreach (var stale in orphaned)
        {
            var fresh = await _prService.GetByIdAsync(repo.Id, stale.Id, ct);
            if (fresh is not null)
            {
                _cache.PullRequests.UpsertCore(fresh);
            }
        }

        var pending = _cache.PullRequests.GetMissingFirstVoteForRepo(repo.Id);
        if (pending.Count == 0)
        {
            task.Value = task.MaxValue;
            return;
        }

        task.MaxValue = pending.Count;
        task.Description = $"[cyan]{repo.Name}[/] [grey]({pending.Count} threads)[/]";

        var fetchTasks = pending.Select(pr => ResolveFirstRequiredVoteAsync(pr, task, ct));
        await Task.WhenAll(fetchTasks);
    }

    private async Task ResolveFirstRequiredVoteAsync(CachedPullRequest pr, ProgressTask task, CancellationToken ct)
    {
        await _fetchSemaphore.WaitAsync(ct);
        try
        {
            var events = await _threadService.GetVoteEventsAsync(pr.RepoId, pr.Id, ct);
            var first = events
                .Where(v => v.VoteValue != 0)
                .Where(v => _requiredReviewerIds.Contains(v.VoterId))
                .Where(v => v.VoterId != pr.AuthorId)
                .OrderBy(v => v.At)
                .FirstOrDefault();

            if (first is not null)
            {
                var elapsed = _clock.ElapsedHours(pr.CreationUtc, first.At);
                _cache.PullRequests.SetFirstRequiredVote(
                    pr.Id, first.VoterId, first.At, first.VoteValue, elapsed, slaMet: elapsed <= _slaHours);
            }
            // No required vote yet — leave nulls; next sync will retry.
        }
        finally
        {
            _fetchSemaphore.Release();
            task.Increment(1);
        }
    }
}
