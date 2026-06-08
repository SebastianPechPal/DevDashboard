using Microsoft.Data.Sqlite;

namespace DevDashboard.Cache;

internal static class CacheSchema
{
    private static readonly string[] MigrationStatements =
    {
        // v1 — initial schema
        """
        CREATE TABLE IF NOT EXISTS repo (
            repo_id TEXT PRIMARY KEY,
            project TEXT NOT NULL,
            name TEXT NOT NULL,
            last_sync_utc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS identity (
            id TEXT PRIMARY KEY,
            descriptor TEXT NOT NULL,
            display_name TEXT NOT NULL,
            email TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS pull_request (
            pr_id INTEGER PRIMARY KEY,
            repo_id TEXT NOT NULL,
            author_id TEXT NULL,
            creation_utc TEXT NOT NULL,
            status TEXT NOT NULL,
            closed_utc TEXT NULL,
            first_required_vote_id TEXT NULL,
            first_required_vote_utc TEXT NULL,
            business_hours_elapsed REAL NULL,
            sla_met INTEGER NULL
        );

        CREATE INDEX IF NOT EXISTS idx_pr_creation ON pull_request(creation_utc);
        CREATE INDEX IF NOT EXISTS idx_pr_status ON pull_request(status);
        CREATE INDEX IF NOT EXISTS idx_pr_repo ON pull_request(repo_id);
        """,

        // v2 — store the actual vote value so the open-PRs view doesn't need to re-fetch threads
        """
        ALTER TABLE pull_request ADD COLUMN first_required_vote_value INTEGER NULL;
        """,

        // v3 — display fields for the open-PRs view
        """
        ALTER TABLE pull_request ADD COLUMN title TEXT NULL;
        ALTER TABLE pull_request ADD COLUMN author_display_name TEXT NULL;
        """,

        // v4 — bug work items for DLR90
        """
        CREATE TABLE IF NOT EXISTS bug_work_item (
            id INTEGER PRIMARY KEY,
            project TEXT NOT NULL,
            title TEXT NULL,
            found_in_system TEXT NULL,
            state TEXT NULL,
            created_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_bug_created ON bug_work_item(created_utc);
        CREATE INDEX IF NOT EXISTS idx_bug_project ON bug_work_item(project);
        """,

        // v5 — per-PR engagement (reviewers + commenters) for the open-PRs panel
        """
        CREATE TABLE IF NOT EXISTS pr_engagement (
            pr_id INTEGER NOT NULL,
            identity_id TEXT NOT NULL,
            display_name TEXT NULL,
            email TEXT NULL,
            kind TEXT NOT NULL,
            first_engagement_utc TEXT NULL,
            PRIMARY KEY (pr_id, identity_id)
        );

        CREATE INDEX IF NOT EXISTS idx_pre_pr ON pr_engagement(pr_id);
        """,

        // v6 — last-activity timestamp for the open-PRs view (latest commit push OR latest text comment)
        """
        ALTER TABLE pull_request ADD COLUMN last_activity_utc TEXT NULL;
        """,

        // v7 — local working-copy path per repo, used as the cwd when launching a background agent
        """
        ALTER TABLE repo ADD COLUMN local_path TEXT NULL;
        """,

        // v8 — current (latest) required-reviewer vote, recomputed every sync. Drives the live
        // open-PR status; the frozen first_required_vote_* columns stay dedicated to the
        // turnaround metric.
        """
        ALTER TABLE pull_request ADD COLUMN current_required_vote_value INTEGER NULL;
        """
    };

    public static void Migrate(SqliteConnection connection)
    {
        EnsureMetaTable(connection);
        var current = GetCurrentVersion(connection);

        for (var v = current; v < MigrationStatements.Length; v++)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = MigrationStatements[v];
            cmd.ExecuteNonQuery();
            SetCurrentVersion(connection, v + 1);
        }
    }

    private static void EnsureMetaTable(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static int GetCurrentVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        return cmd.ExecuteScalar() is string v ? int.Parse(v) : 0;
    }

    private static void SetCurrentVersion(SqliteConnection connection, int version)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO meta (key, value) VALUES ('schema_version', $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$v", version.ToString());
        cmd.ExecuteNonQuery();
    }
}
