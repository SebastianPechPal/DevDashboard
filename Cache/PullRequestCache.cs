using Microsoft.Data.Sqlite;

namespace CLIGoalHelper.Cache;

public sealed class PullRequestCache
{
    private readonly SqliteConnection _connection;
    private readonly object _writeLock = new();

    internal PullRequestCache(SqliteConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Inserts a new PR or refreshes core fields (status, closed_utc, etc.) on an existing one.
    /// Vote fields are written only on initial INSERT — on conflict they're preserved so the
    /// sync flow can refetch PR metadata without clobbering computed first-vote attribution.
    /// </summary>
    public void UpsertCore(CachedPullRequest pr)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO pull_request (
                    pr_id, repo_id, title, author_id, author_display_name,
                    creation_utc, status, closed_utc,
                    first_required_vote_id, first_required_vote_utc, first_required_vote_value,
                    business_hours_elapsed, sla_met)
                VALUES (
                    $id, $repo, $title, $author, $authorName,
                    $creation, $status, $closed,
                    NULL, NULL, NULL, NULL, NULL)
                ON CONFLICT(pr_id) DO UPDATE SET
                    repo_id = excluded.repo_id,
                    title = excluded.title,
                    author_id = excluded.author_id,
                    author_display_name = excluded.author_display_name,
                    creation_utc = excluded.creation_utc,
                    status = excluded.status,
                    closed_utc = excluded.closed_utc;
                """;
            cmd.Parameters.AddWithValue("$id", pr.Id);
            cmd.Parameters.AddWithValue("$repo", pr.RepoId);
            cmd.Parameters.AddWithValue("$title", (object?)pr.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$author", (object?)pr.AuthorId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$authorName", (object?)pr.AuthorDisplayName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$creation", pr.CreationUtc.UtcDateTime.ToString("o"));
            cmd.Parameters.AddWithValue("$status", pr.Status.ToString());
            cmd.Parameters.AddWithValue("$closed", (object?)pr.ClosedUtc?.UtcDateTime.ToString("o") ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetFirstRequiredVote(int prId, string voteId, DateTimeOffset voteAt, int voteValue, double businessHoursElapsed, bool slaMet)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE pull_request SET
                    first_required_vote_id = $voteId,
                    first_required_vote_utc = $voteUtc,
                    first_required_vote_value = $voteValue,
                    business_hours_elapsed = $elapsed,
                    sla_met = $sla
                WHERE pr_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", prId);
            cmd.Parameters.AddWithValue("$voteId", voteId);
            cmd.Parameters.AddWithValue("$voteUtc", voteAt.UtcDateTime.ToString("o"));
            cmd.Parameters.AddWithValue("$voteValue", voteValue);
            cmd.Parameters.AddWithValue("$elapsed", businessHoursElapsed);
            cmd.Parameters.AddWithValue("$sla", slaMet ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    public CachedPullRequest? GetById(int id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = SelectClause + " WHERE pr_id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public List<CachedPullRequest> GetActiveForRepo(string repoId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = SelectClause + " WHERE repo_id = $repo AND status = 'Active';";
        cmd.Parameters.AddWithValue("$repo", repoId);
        return ReadAll(cmd);
    }

    public List<CachedPullRequest> GetMissingFirstVoteForRepo(string repoId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = SelectClause + " WHERE repo_id = $repo AND first_required_vote_utc IS NULL;";
        cmd.Parameters.AddWithValue("$repo", repoId);
        return ReadAll(cmd);
    }

    public DateTimeOffset? GetLatestClosedUtc(string repoId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT MAX(closed_utc) FROM pull_request
            WHERE repo_id = $repo AND status IN ('Completed', 'Abandoned');
            """;
        cmd.Parameters.AddWithValue("$repo", repoId);
        return cmd.ExecuteScalar() is string s ? DateTimeOffset.Parse(s) : null;
    }

    public int CountAll()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pull_request;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public sealed record TrailingWindowCounts(int Total, int Pass, int Fail, int NoVote);

    public TrailingWindowCounts TrailingWindow(DateTimeOffset since)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*),
                COUNT(CASE WHEN sla_met = 1 THEN 1 END),
                COUNT(CASE WHEN sla_met = 0 THEN 1 END),
                COUNT(CASE WHEN first_required_vote_utc IS NULL THEN 1 END)
            FROM pull_request
            WHERE creation_utc >= $since AND status = 'Completed';
            """;
        cmd.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("o"));
        using var r = cmd.ExecuteReader();
        r.Read();
        return new TrailingWindowCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));
    }

    public sealed record MonthBucket(string YearMonth, int Total, int Pass);

    public List<MonthBucket> MonthlyBuckets(DateTimeOffset since)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                substr(creation_utc, 1, 7) AS month,
                COUNT(*),
                COUNT(CASE WHEN sla_met = 1 THEN 1 END)
            FROM pull_request
            WHERE creation_utc >= $since AND status = 'Completed'
            GROUP BY month
            ORDER BY month;
            """;
        cmd.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("o"));
        using var r = cmd.ExecuteReader();
        var results = new List<MonthBucket>();
        while (r.Read())
        {
            results.Add(new MonthBucket(r.GetString(0), r.GetInt32(1), r.GetInt32(2)));
        }
        return results;
    }

    public double? MedianBusinessHours(DateTimeOffset since)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT business_hours_elapsed FROM pull_request
            WHERE creation_utc >= $since AND status = 'Completed' AND business_hours_elapsed IS NOT NULL
            ORDER BY business_hours_elapsed;
            """;
        cmd.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("o"));
        using var r = cmd.ExecuteReader();
        var values = new List<double>();
        while (r.Read())
        {
            values.Add(r.GetDouble(0));
        }
        if (values.Count == 0) return null;
        return values.Count % 2 == 1
            ? values[values.Count / 2]
            : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2.0;
    }

    public int CountFirstVotesBy(string voterId, DateTimeOffset since)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM pull_request
            WHERE creation_utc >= $since AND first_required_vote_id = $vid;
            """;
        cmd.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("o"));
        cmd.Parameters.AddWithValue("$vid", voterId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private const string SelectClause = """
        SELECT pr_id, repo_id, title, author_id, author_display_name,
               creation_utc, status, closed_utc,
               first_required_vote_id, first_required_vote_utc, first_required_vote_value,
               business_hours_elapsed, sla_met
        FROM pull_request
        """;

    private static List<CachedPullRequest> ReadAll(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var results = new List<CachedPullRequest>();
        while (reader.Read())
        {
            results.Add(Map(reader));
        }
        return results;
    }

    private static CachedPullRequest Map(SqliteDataReader r)
    {
        return new CachedPullRequest(
            Id: r.GetInt32(0),
            RepoId: r.GetString(1),
            Title: r.IsDBNull(2) ? null : r.GetString(2),
            AuthorId: r.IsDBNull(3) ? null : r.GetString(3),
            AuthorDisplayName: r.IsDBNull(4) ? null : r.GetString(4),
            CreationUtc: DateTimeOffset.Parse(r.GetString(5)),
            Status: Enum.Parse<PullRequestStatus>(r.GetString(6)),
            ClosedUtc: r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7)),
            FirstRequiredVoteId: r.IsDBNull(8) ? null : r.GetString(8),
            FirstRequiredVoteUtc: r.IsDBNull(9) ? null : DateTimeOffset.Parse(r.GetString(9)),
            FirstRequiredVoteValue: r.IsDBNull(10) ? null : r.GetInt32(10),
            BusinessHoursElapsed: r.IsDBNull(11) ? null : r.GetDouble(11),
            SlaMet: r.IsDBNull(12) ? null : r.GetInt32(12) == 1);
    }
}
