using Microsoft.Data.Sqlite;

namespace DevDashboard.Cache;

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

    public sealed record DlrCounts(int Total, int Production, int QaTest, int Dev, int Unknown);

    /// <summary>
    /// Counts work items in a rolling window [since, until], excluding state 'Removed'.
    /// Found-system buckets follow the Bug-Report-Guidelines wiki: Production (customer-found),
    /// QA + Test (pre-release / test activities — grouped here), Dev (nightly). Anything else,
    /// including an unset field, counts as Unknown.
    /// </summary>
    public DlrCounts CountInWindow(DateTimeOffset since, DateTimeOffset until)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*),
                COUNT(CASE WHEN found_in_system = 'Production' THEN 1 END),
                COUNT(CASE WHEN found_in_system IN ('QA', 'Test') THEN 1 END),
                COUNT(CASE WHEN found_in_system = 'Dev' THEN 1 END),
                COUNT(CASE WHEN found_in_system IS NULL
                            OR found_in_system NOT IN ('Production', 'QA', 'Test', 'Dev') THEN 1 END)
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

    public sealed record RecentBug(int Id, string Project, string? Title, string? FoundInSystem, string? State, DateTimeOffset CreatedUtc);

    /// <summary>
    /// The most recently created bugs/issues, newest first, limited to <paramref name="count"/>.
    /// Excludes state 'Removed' — the same filter as <see cref="CountInWindow"/> — so the list
    /// agrees with the DLR counts. (created_utc is stored round-trippable, so a lexical DESC sort
    /// is chronological.) Project is returned so callers can build the work-item URL; State drives
    /// the status emoji in the panel.
    /// </summary>
    public IReadOnlyList<RecentBug> GetRecent(int count)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, project, title, found_in_system, state, created_utc
            FROM bug_work_item
            WHERE state IS NULL OR state <> 'Removed'
            ORDER BY created_utc DESC
            LIMIT $count;
            """;
        cmd.Parameters.AddWithValue("$count", count);

        using var r = cmd.ExecuteReader();
        var results = new List<RecentBug>();
        while (r.Read())
        {
            results.Add(new RecentBug(
                Id: r.GetInt32(0),
                Project: r.GetString(1),
                Title: r.IsDBNull(2) ? null : r.GetString(2),
                FoundInSystem: r.IsDBNull(3) ? null : r.GetString(3),
                State: r.IsDBNull(4) ? null : r.GetString(4),
                CreatedUtc: IsoParse.Offset(r.GetString(5))));
        }
        return results;
    }

    public sealed record MonthBugCount(string YearMonth, int Count);

    /// <summary>
    /// Bugs/issues created in each calendar month at or after <paramref name="since"/>, keyed by
    /// "yyyy-MM" (the created_utc prefix), excluding state 'Removed'. Mirrors the PR
    /// <c>MonthlyBuckets</c> shape so the two series can be aligned month-for-month.
    /// </summary>
    public IReadOnlyList<MonthBugCount> MonthlyCounts(DateTimeOffset since)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT substr(created_utc, 1, 7) AS month, COUNT(*)
            FROM bug_work_item
            WHERE created_utc >= $since AND (state IS NULL OR state <> 'Removed')
            GROUP BY month
            ORDER BY month;
            """;
        cmd.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("o"));

        using var r = cmd.ExecuteReader();
        var results = new List<MonthBugCount>();
        while (r.Read())
        {
            results.Add(new MonthBugCount(r.GetString(0), r.GetInt32(1)));
        }
        return results;
    }
}
