using System.Text.Json;
using DevDashboard.Cache;

namespace DevDashboard.Ado;

public sealed record PrReviewer(string Id, string? DisplayName, string? Email, bool IsContainer);

public sealed record ActivePrSnapshot(CachedPullRequest Pr, IReadOnlyList<PrReviewer> Reviewers);

public sealed class PullRequestService
{
    private const int PageSize = 1000;

    private readonly AdoClient _client;
    private readonly string _organizationUrl;

    public PullRequestService(AdoClient client, string organizationUrl)
    {
        _client = client;
        _organizationUrl = organizationUrl.TrimEnd('/');
    }

    /// <summary>
    /// Lists active PRs together with their current reviewers. The ADO list endpoint returns
    /// reviewers inline, so this is one HTTP call per page just like <see cref="ListAsync"/>.
    /// Container/group reviewer entries are included unfiltered so callers decide what to do.
    /// </summary>
    public async Task<List<ActivePrSnapshot>> ListActiveWithReviewersAsync(
        string repoId, CancellationToken ct = default)
    {
        var results = new List<ActivePrSnapshot>();
        var skip = 0;

        while (true)
        {
            var url = BuildUrl(repoId, PullRequestStatus.Active, minTime: null, skip);
            using var doc = await _client.GetJsonAsync(url, ct);

            var pageCount = 0;
            foreach (var pr in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                results.Add(new ActivePrSnapshot(Map(pr, repoId), MapReviewers(pr)));
                pageCount++;
            }

            if (pageCount < PageSize)
            {
                return results;
            }
            skip += PageSize;
        }
    }

    private static List<PrReviewer> MapReviewers(JsonElement pr)
    {
        if (!pr.TryGetProperty("reviewers", out var rs) || rs.ValueKind != JsonValueKind.Array)
        {
            return new List<PrReviewer>();
        }
        var list = new List<PrReviewer>();
        foreach (var r in rs.EnumerateArray())
        {
            if (!r.TryGetProperty("id", out var idEl)) continue;
            var id = idEl.GetString();
            if (string.IsNullOrEmpty(id)) continue;

            var displayName = r.TryGetProperty("displayName", out var d) ? d.GetString() : null;
            var email = r.TryGetProperty("uniqueName", out var e) ? e.GetString() : null;
            var isContainer = r.TryGetProperty("isContainer", out var c) && c.ValueKind == JsonValueKind.True;
            list.Add(new PrReviewer(id, displayName, email, isContainer));
        }
        return list;
    }

    /// <summary>
    /// Lists pull requests for a repo at the given status. For completed/abandoned PRs the
    /// `minTime` filter applies to closedDate; for active PRs it applies to creationDate.
    /// Pages through results until the API returns fewer than <see cref="PageSize"/> rows.
    /// </summary>
    /// <summary>
    /// Fetches a single PR by id. Returns null if ADO 404s (e.g., the PR was deleted).
    /// </summary>
    public async Task<CachedPullRequest?> GetByIdAsync(string repoId, int pullRequestId, CancellationToken ct = default)
    {
        var url = $"{_organizationUrl}/_apis/git/repositories/{repoId}/pullrequests/{pullRequestId}?api-version=7.1";
        try
        {
            using var doc = await _client.GetJsonAsync(url, ct);
            return Map(doc.RootElement, repoId);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<List<CachedPullRequest>> ListAsync(
        string repoId,
        PullRequestStatus status,
        DateTimeOffset? minTime = null,
        CancellationToken ct = default)
    {
        var results = new List<CachedPullRequest>();
        var skip = 0;

        while (true)
        {
            var url = BuildUrl(repoId, status, minTime, skip);
            using var doc = await _client.GetJsonAsync(url, ct);

            var pageCount = 0;
            foreach (var pr in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                results.Add(Map(pr, repoId));
                pageCount++;
            }

            if (pageCount < PageSize)
            {
                return results;
            }
            skip += PageSize;
        }
    }

    private string BuildUrl(string repoId, PullRequestStatus status, DateTimeOffset? minTime, int skip)
    {
        var statusParam = status switch
        {
            PullRequestStatus.Active => "active",
            PullRequestStatus.Completed => "completed",
            PullRequestStatus.Abandoned => "abandoned",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

        var url = $"{_organizationUrl}/_apis/git/repositories/{repoId}/pullrequests"
            + $"?searchCriteria.status={statusParam}"
            + $"&$top={PageSize}"
            + $"&$skip={skip}"
            + "&api-version=7.1";

        if (minTime.HasValue)
        {
            url += $"&searchCriteria.minTime={Uri.EscapeDataString(minTime.Value.UtcDateTime.ToString("o"))}";
        }
        return url;
    }

    private static CachedPullRequest Map(JsonElement pr, string repoId)
    {
        var status = pr.GetProperty("status").GetString() switch
        {
            "active" => PullRequestStatus.Active,
            "completed" => PullRequestStatus.Completed,
            "abandoned" => PullRequestStatus.Abandoned,
            var other => throw new InvalidOperationException($"Unknown PR status '{other}'")
        };

        string? authorId = null;
        string? authorName = null;
        if (pr.TryGetProperty("createdBy", out var by) && by.ValueKind == JsonValueKind.Object)
        {
            authorId = by.TryGetProperty("id", out var byId) ? byId.GetString() : null;
            authorName = by.TryGetProperty("displayName", out var byName) ? byName.GetString() : null;
        }

        var title = pr.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;

        var closed = pr.TryGetProperty("closedDate", out var cd) && cd.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(cd.GetString()!)
            : (DateTimeOffset?)null;

        return new CachedPullRequest(
            Id: pr.GetProperty("pullRequestId").GetInt32(),
            RepoId: repoId,
            Title: title,
            AuthorId: authorId,
            AuthorDisplayName: authorName,
            CreationUtc: DateTimeOffset.Parse(pr.GetProperty("creationDate").GetString()!),
            Status: status,
            ClosedUtc: closed,
            FirstRequiredVoteId: null,
            FirstRequiredVoteUtc: null,
            FirstRequiredVoteValue: null,
            BusinessHoursElapsed: null,
            SlaMet: null,
            LastActivityUtc: null);
    }
}
