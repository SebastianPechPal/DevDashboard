namespace DevDashboard.Ado;

public sealed record AdoRepository(string Id, string Name, string Project);

public sealed class RepositoryService
{
    private readonly AdoClient _client;
    private readonly string _organizationUrl;

    public RepositoryService(AdoClient client, string organizationUrl)
    {
        _client = client;
        _organizationUrl = organizationUrl.TrimEnd('/');
    }

    /// <summary>
    /// Lists all git repositories visible at the organization level (across projects).
    /// </summary>
    public async Task<List<AdoRepository>> ListAllAsync(CancellationToken ct = default)
    {
        var url = $"{_organizationUrl}/_apis/git/repositories?api-version=7.1";
        using var doc = await _client.GetJsonAsync(url, ct);

        var results = new List<AdoRepository>();
        foreach (var repo in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            results.Add(new AdoRepository(
                Id: repo.GetProperty("id").GetString()!,
                Name: repo.GetProperty("name").GetString()!,
                Project: repo.GetProperty("project").GetProperty("name").GetString()!));
        }
        return results;
    }
}
