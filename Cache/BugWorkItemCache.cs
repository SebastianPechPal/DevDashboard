using Microsoft.Data.Sqlite;

namespace CLIGoalHelper.Cache;

public sealed class BugWorkItemCache
{
    private readonly SqliteConnection _connection;
    private readonly object _writeLock = new();

    internal BugWorkItemCache(SqliteConnection connection)
    {
        _connection = connection;
    }

    public void Upsert(CachedBugWorkItem bug)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO bug_work_item (id, project, title, found_in_system, state, created_utc)
                VALUES ($id, $project, $title, $foundIn, $state, $created)
                ON CONFLICT(id) DO UPDATE SET
                    project = excluded.project,
                    title = excluded.title,
                    found_in_system = excluded.found_in_system,
                    state = excluded.state,
                    created_utc = excluded.created_utc;
                """;
            cmd.Parameters.AddWithValue("$id", bug.Id);
            cmd.Parameters.AddWithValue("$project", bug.Project);
            cmd.Parameters.AddWithValue("$title", (object?)bug.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$foundIn", (object?)bug.FoundInSystem ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$state", (object?)bug.State ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", bug.CreatedUtc.UtcDateTime.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    public int CountAll()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM bug_work_item;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public sealed record DlrCounts(int Total, int Production, int Test, int Dev, int Unknown);

    /// <summary>
    /// Counts work items in a rolling window [since, until], applying PBI's filters:
    /// state != 'Removed'. Mirrors the KPI_DefectsActual logic.
    /// </summary>
    public DlrCounts CountInWindow(DateTimeOffset since, DateTimeOffset until)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*),
                COUNT(CASE WHEN found_in_system = 'Production' THEN 1 END),
                COUNT(CASE WHEN found_in_system = 'Test' THEN 1 END),
                COUNT(CASE WHEN found_in_system = 'Dev' THEN 1 END),
                COUNT(CASE WHEN found_in_system IS NULL
                            OR found_in_system NOT IN ('Production', 'Test', 'Dev') THEN 1 END)
            FROM bug_work_item
            WHERE created_utc >= $since
              AND created_utc <= $until
              AND (state IS NULL OR state <> 'Removed');
            """;
        cmd.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("o"));
        cmd.Parameters.AddWithValue("$until", until.UtcDateTime.ToString("o"));
        using var r = cmd.ExecuteReader();
        r.Read();
        return new DlrCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
    }

}
