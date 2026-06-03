using Microsoft.Data.Sqlite;

namespace DevDashboard.Cache;

public sealed class MetaCache
{
    private readonly SqliteConnection _connection;

    internal MetaCache(SqliteConnection connection)
    {
        _connection = connection;
    }

    public string? Get(string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO meta (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
