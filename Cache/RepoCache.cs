using Microsoft.Data.Sqlite;

namespace CLIGoalHelper.Cache;

public sealed class RepoCache
{
    private readonly SqliteConnection _connection;

    internal RepoCache(SqliteConnection connection)
    {
        _connection = connection;
    }

    public void Upsert(CachedRepo repo)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO repo (repo_id, project, name, last_sync_utc)
            VALUES ($id, $project, $name, $lastSync)
            ON CONFLICT(repo_id) DO UPDATE SET
                project = excluded.project,
                name = excluded.name,
                last_sync_utc = excluded.last_sync_utc;
            """;
        cmd.Parameters.AddWithValue("$id", repo.Id);
        cmd.Parameters.AddWithValue("$project", repo.Project);
        cmd.Parameters.AddWithValue("$name", repo.Name);
        cmd.Parameters.AddWithValue("$lastSync", (object?)repo.LastSyncUtc?.UtcDateTime.ToString("o") ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public CachedRepo? GetByName(string name)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT repo_id, project, name, last_sync_utc FROM repo WHERE name = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public List<CachedRepo> GetAll()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT repo_id, project, name, last_sync_utc FROM repo;";
        using var reader = cmd.ExecuteReader();
        var results = new List<CachedRepo>();
        while (reader.Read())
        {
            results.Add(Map(reader));
        }
        return results;
    }

    public void SetLastSync(string repoId, DateTimeOffset lastSync)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE repo SET last_sync_utc = $ts WHERE repo_id = $id;";
        cmd.Parameters.AddWithValue("$ts", lastSync.UtcDateTime.ToString("o"));
        cmd.Parameters.AddWithValue("$id", repoId);
        cmd.ExecuteNonQuery();
    }

    private static CachedRepo Map(SqliteDataReader r)
    {
        return new CachedRepo(
            Id: r.GetString(0),
            Project: r.GetString(1),
            Name: r.GetString(2),
            LastSyncUtc: r.IsDBNull(3) ? null : DateTimeOffset.Parse(r.GetString(3)));
    }
}
