using DevDashboard.Ado;
using DevDashboard.BusinessTime;
using DevDashboard.Cache;
using Spectre.Console;

namespace DevDashboard.Sync;

/// <summary>
/// Orchestrates a sync pass: pulls PR metadata for each repo, then for each active PR
/// fetches threads once to derive both first-required-vote attribution and the current
/// engagement set (reviewers + commenters). Non-active PRs missing a first-required-vote
/// still get a thread fetch on the vote-only path. Bounded concurrency on thread fetches
/// keeps the ADO load reasonable.
/// </summary>
public sealed class SyncService
{
    private const int ThreadFetchConcurrency = 10;

    private readonly CacheStore _cache;
    private readonly PullRequestService _prService;
    private readonly ThreadService _threadService;
    private readonly IterationService _iterationService;
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
        IterationService iterationService,
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
        _iterationService = iterationService;
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
        // Snapshot the previous full-sync completion time before this run starts. The dashboard
        // uses this to highlight PRs whose activity advanced between the prior sync and this one.
        // First-ever sync: no prior value, so previous stays unset and nothing highlights.
        var priorLast = _cache.Meta.Get(LastFullSyncMetaKey);
        if (priorLast != null)
        {
            _cache.Meta.Set(PreviousFullSyncMetaKey, priorLast);
        }

        foreach (var repo in _cache.Repos.GetAll())
        {
            await SyncRepoAsync(repo, progress, ct);
            _cache.Repos.SetLastSync(repo.Id, DateTimeOffset.UtcNow);
        }
        await SyncBugsAsync(progress, ct);

        _cache.Meta.Set(LastFullSyncMetaKey, DateTimeOffset.UtcNow.UtcDateTime.ToString("o"));
    }

    public const string LastFullSyncMetaKey = "last_full_sync_completed_utc";
    public const string PreviousFullSyncMetaKey = "previous_full_sync_completed_utc";

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

        var activeSnapshots = await _prService.ListActiveWithReviewersAsync(repo.Id, ct);
        var completed = await _prService.ListAsync(repo.Id, PullRequestStatus.Completed, minTime, ct);
        var abandoned = await _prService.ListAsync(repo.Id, PullRequestStatus.Abandoned, minTime, ct);

        foreach (var snap in activeSnapshots)
        {
            _cache.PullRequests.UpsertCore(snap.Pr);
        }
        foreach (var pr in completed.Concat(abandoned))
        {
            _cache.PullRequests.UpsertCore(pr);
        }

        // Reconcile orphaned actives: cache rows still marked Active that ADO no longer
        // returns as active. Happens when a PR closes between two syncs and its closedDate
        // ends up earlier than the next sync's minTime. Re-fetch each individually.
        var liveActiveIds = activeSnapshots.Select(s => s.Pr.Id).ToHashSet();
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

        // Active PRs always get a thread fetch (engagements need to refresh each sync).
        // Non-active PRs missing a first-required-vote take the vote-only path.
        var pendingVoteOnly = _cache.PullRequests.GetMissingFirstVoteForRepo(repo.Id)
            .Where(p => !liveActiveIds.Contains(p.Id))
            .ToList();

        var totalFetches = activeSnapshots.Count + pendingVoteOnly.Count;
        if (totalFetches == 0)
        {
            task.Value = task.MaxValue;
            return;
        }

        task.MaxValue = totalFetches;
        task.Description = $"[cyan]{repo.Name}[/] [grey]({totalFetches} threads)[/]";

        var fetchTasks = new List<Task>(totalFetches);
        foreach (var snap in activeSnapshots)
        {
            fetchTasks.Add(ProcessActiveAsync(snap, task, ct));
        }
        foreach (var pr in pendingVoteOnly)
        {
            fetchTasks.Add(ProcessVoteOnlyAsync(pr, task, ct));
        }
        await Task.WhenAll(fetchTasks);
    }

    private async Task ProcessActiveAsync(ActivePrSnapshot snap, ProgressTask task, CancellationToken ct)
    {
        await _fetchSemaphore.WaitAsync(ct);
        try
        {
            var threadsTask = _threadService.GetThreadActivityAsync(snap.Pr.RepoId, snap.Pr.Id, ct);
            var latestIterationTask = _iterationService.GetLatestIterationDateAsync(snap.Pr.RepoId, snap.Pr.Id, ct);
            await Task.WhenAll(threadsTask, latestIterationTask);
            var activity = threadsTask.Result;
            var latestIteration = latestIterationTask.Result;

            if (snap.Pr.FirstRequiredVoteUtc is null)
            {
                ApplyFirstRequiredVote(snap.Pr, activity.Votes);
            }

            ApplyLastActivity(snap.Pr, latestIteration, activity.LatestCommentUtc);

            var engagements = BuildEngagements(snap.Pr, snap.Reviewers, activity);
            _cache.PullRequests.ReplaceEngagementsFor(snap.Pr.Id, engagements);
        }
        finally
        {
            _fetchSemaphore.Release();
            task.Increment(1);
        }
    }

    private async Task ProcessVoteOnlyAsync(CachedPullRequest pr, ProgressTask task, CancellationToken ct)
    {
        await _fetchSemaphore.WaitAsync(ct);
        try
        {
            var activity = await _threadService.GetThreadActivityAsync(pr.RepoId, pr.Id, ct);
            ApplyFirstRequiredVote(pr, activity.Votes);
        }
        finally
        {
            _fetchSemaphore.Release();
            task.Increment(1);
        }
    }

    private void ApplyLastActivity(CachedPullRequest pr, DateTimeOffset? latestIteration, DateTimeOffset? latestComment)
    {
        // Iteration 1 is created at PR creation, so latestIteration normally exists and is >= pr.CreationUtc.
        // We pick the max of the two signals; if both are null (iteration fetch failed and no comments),
        // fall back to CreationUtc so the column always renders.
        DateTimeOffset? candidate = null;
        if (latestIteration.HasValue) candidate = latestIteration;
        if (latestComment.HasValue && (candidate is null || latestComment > candidate)) candidate = latestComment;
        var last = candidate ?? pr.CreationUtc;
        _cache.PullRequests.SetLastActivity(pr.Id, last);
    }
    private void ApplyFirstRequiredVote(CachedPullRequest pr, IReadOnlyList<VoteEvent> votes)
    {
        var first = votes
            .Where(v => v.VoteValue != 0)
            .Where(v => _requiredReviewerIds.Contains(v.VoterId))
            .Where(v => v.VoterId != pr.AuthorId)
            .OrderBy(v => v.At)
            .FirstOrDefault();

        if (first is null) return;

        var elapsed = _clock.ElapsedHours(pr.CreationUtc, first.At);
        _cache.PullRequests.SetFirstRequiredVote(
            pr.Id, first.VoterId, first.At, first.VoteValue, elapsed, slaMet: elapsed <= _slaHours);
    }

    private static List<CachedPrEngagement> BuildEngagements(
        CachedPullRequest pr,
        IReadOnlyList<PrReviewer> reviewers,
        ThreadActivity activity)
    {
        // Reviewers (green) come from the PR's reviewers array, excluding container/group entries
        // and the author themselves. Commenters (red) come from non-vote threads; if someone is
        // both a reviewer and a commenter, the reviewer classification wins.
        var earliestActionById = new Dictionary<string, DateTimeOffset>();
        foreach (var v in activity.Votes.Where(v => v.VoteValue != 0))
        {
            TrackEarliest(earliestActionById, v.VoterId, v.At);
        }
        foreach (var c in activity.Commenters)
        {
            TrackEarliest(earliestActionById, c.Id, c.FirstCommentAt);
        }

        var result = new Dictionary<string, CachedPrEngagement>();

        foreach (var r in reviewers)
        {
            if (r.IsContainer) continue;
            if (r.Id == pr.AuthorId) continue;
            DateTimeOffset? at = earliestActionById.TryGetValue(r.Id, out var t) ? t : null;
            result[r.Id] = new CachedPrEngagement(
                pr.Id, r.Id, r.DisplayName, r.Email, EngagementKind.Reviewer, at);
        }

        foreach (var c in activity.Commenters)
        {
            if (c.Id == pr.AuthorId) continue;
            if (result.ContainsKey(c.Id)) continue;
            result[c.Id] = new CachedPrEngagement(
                pr.Id, c.Id, c.DisplayName, c.Email, EngagementKind.Commenter, c.FirstCommentAt);
        }

        return result.Values.ToList();
    }

    private static void TrackEarliest(Dictionary<string, DateTimeOffset> map, string id, DateTimeOffset at)
    {
        if (!map.TryGetValue(id, out var existing) || at < existing)
        {
            map[id] = at;
        }
    }
}
