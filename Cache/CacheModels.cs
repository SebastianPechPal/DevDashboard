namespace CLIGoalHelper.Cache;

public sealed record CachedRepo(string Id, string Project, string Name, DateTimeOffset? LastSyncUtc);

public sealed record CachedIdentity(string Id, string Descriptor, string DisplayName, string? Email);

public enum PullRequestStatus
{
    Active,
    Completed,
    Abandoned
}

public sealed record CachedBugWorkItem(
    int Id,
    string Project,
    string? Title,
    string? FoundInSystem,
    string? State,
    DateTimeOffset CreatedUtc);

public sealed record CachedPullRequest(
    int Id,
    string RepoId,
    string? Title,
    string? AuthorId,
    string? AuthorDisplayName,
    DateTimeOffset CreationUtc,
    PullRequestStatus Status,
    DateTimeOffset? ClosedUtc,
    string? FirstRequiredVoteId,
    DateTimeOffset? FirstRequiredVoteUtc,
    int? FirstRequiredVoteValue,
    double? BusinessHoursElapsed,
    bool? SlaMet);
