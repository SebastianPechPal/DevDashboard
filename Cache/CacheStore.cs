using Microsoft.Data.Sqlite;

namespace CLIGoalHelper.Cache;

public sealed class CacheStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public CacheStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        DatabasePath = databasePath;
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();

        EnableWal();
        CacheSchema.Migrate(_connection);

        Repos = new RepoCache(_connection);
        Identities = new IdentityCache(_connection);
        PullRequests = new PullRequestCache(_connection);
        Bugs = new BugWorkItemCache(_connection);
        Meta = new MetaCache(_connection);
    }

    public static CacheStore OpenDefault()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(root, "DevDashboard", "cache.db");
        return new CacheStore(path);
    }

    public string DatabasePath { get; }
    public RepoCache Repos { get; }
    public IdentityCache Identities { get; }
    public PullRequestCache PullRequests { get; }
    public BugWorkItemCache Bugs { get; }
    public MetaCache Meta { get; }

    private void EnableWal()
    {
        // WAL gives us better read concurrency and safer crashes than rollback mode.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
