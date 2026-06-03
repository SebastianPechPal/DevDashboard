namespace DevDashboard.Cache;

public sealed record CachedRepo(string Id, string Project, string Name, DateTimeOffset? LastSyncUtc, string? LocalPath = null);

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
    bool? SlaMet,
    DateTimeOffset? LastActivityUtc);

public enum EngagementKind
{
    Reviewer,
    Commenter
}

public sealed record CachedPrEngagement(
    int PrId,
    string IdentityId,
    string? DisplayName,
    string? Email,
    EngagementKind Kind,
    DateTimeOffset? FirstEngagementUtc);
