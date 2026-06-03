using System.Text.Json;
using DevDashboard.Cache;

namespace DevDashboard.Ado;

public sealed class WorkItemService
{
    private const int BatchSize = 200;

    private readonly AdoClient _client;
    private readonly string _organizationUrl;

    public WorkItemService(AdoClient client, string organizationUrl)
    {
        _client = client;
        _organizationUrl = organizationUrl.TrimEnd('/');
    }

    /// <summary>
    /// Returns every Bug and Issue work item in the given project created on or after `since`,
    /// excluding Removed-state items. Both types contribute to DLR90 in PBI's KPI_DefectsActual
    /// model, so we include both. Verified against the source-of-truth report.
    /// </summary>
    public async Task<List<CachedBugWorkItem>> GetBugsAsync(
        string project,
        DateTimeOffset since,
        CancellationToken ct = default)
    {
        var ids = await QueryWorkItemIdsAsync(project, since, ct);
        if (ids.Count == 0)
        {
            return new List<CachedBugWorkItem>();
        }

        var results = new List<CachedBugWorkItem>(ids.Count);
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            results.AddRange(await FetchBatchAsync(project, chunk, ct));
        }
        return results;
    }

    private async Task<List<int>> QueryWorkItemIdsAsync(string project, DateTimeOffset since, CancellationToken ct)
    {
        var wiqlBody = JsonSerializer.Serialize(new
        {
            query =
                "SELECT [System.Id] FROM workitems "
                + "WHERE [System.WorkItemType] IN ('Bug', 'Issue') "
                + $"AND [System.TeamProject] = '{project}' "
                + "AND [System.State] <> 'Removed' "
                + $"AND [System.CreatedDate] >= '{since.UtcDateTime:yyyy-MM-dd}'"
        });

        var url = $"{_organizationUrl}/{project}/_apis/wit/wiql?api-version=7.1";
        using var doc = await _client.PostJsonAsync(url, wiqlBody, ct);

        return doc.RootElement.GetProperty("workItems")
            .EnumerateArray()
            .Select(w => w.GetProperty("id").GetInt32())
            .ToList();
    }

    private async Task<List<CachedBugWorkItem>> FetchBatchAsync(string project, int[] ids, CancellationToken ct)
    {
        const string fields = "System.Id,System.Title,System.State,System.CreatedDate,Custom.FoundinSystem";
        var url = $"{_organizationUrl}/{project}/_apis/wit/workitems"
            + $"?ids={string.Join(",", ids)}&fields={fields}&api-version=7.1";

        using var doc = await _client.GetJsonAsync(url, ct);

        var results = new List<CachedBugWorkItem>();
        foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var f = item.GetProperty("fields");
            results.Add(new CachedBugWorkItem(
                Id: item.GetProperty("id").GetInt32(),
                Project: project,
                Title: TryGetString(f, "System.Title"),
                FoundInSystem: TryGetString(f, "Custom.FoundinSystem"),
                State: TryGetString(f, "System.State"),
                CreatedUtc: DateTimeOffset.Parse(f.GetProperty("System.CreatedDate").GetString()!)));
        }
        return results;
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) ? value.GetString() : null;
    }
}
