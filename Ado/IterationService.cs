using System.Text.Json;

namespace CLIGoalHelper.Ado;

public sealed class IterationService
{
    private readonly AdoClient _client;
    private readonly string _organizationUrl;

    public IterationService(AdoClient client, string organizationUrl)
    {
        _client = client;
        _organizationUrl = organizationUrl.TrimEnd('/');
    }

    /// <summary>
    /// Returns the createdDate of the most recent iteration for a PR, which corresponds to the
    /// last push to the source branch. Iteration 1 (= PR creation) is included, so a freshly
    /// created PR with no later pushes still gets a timestamp equal to its CreationUtc.
    /// Returns null on 404 (PR deleted) or if the iterations array is empty.
    /// </summary>
    public async Task<DateTimeOffset?> GetLatestIterationDateAsync(
        string repoId, int pullRequestId, CancellationToken ct = default)
    {
        var url = $"{_organizationUrl}/_apis/git/repositories/{repoId}"
            + $"/pullrequests/{pullRequestId}/iterations?api-version=7.1";

        try
        {
            using var doc = await _client.GetJsonAsync(url, ct);
            DateTimeOffset? latest = null;
            foreach (var it in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                if (!it.TryGetProperty("createdDate", out var cd)) continue;
                if (cd.ValueKind != JsonValueKind.String) continue;
                var at = DateTimeOffset.Parse(cd.GetString()!);
                if (latest is null || at > latest) latest = at;
            }
            return latest;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
