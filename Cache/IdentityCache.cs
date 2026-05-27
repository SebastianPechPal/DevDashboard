using Microsoft.Data.Sqlite;

namespace CLIGoalHelper.Cache;

public sealed class IdentityCache
{
    private readonly SqliteConnection _connection;

    internal IdentityCache(SqliteConnection connection)
    {
        _connection = connection;
    }

    public void Upsert(CachedIdentity identity)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO identity (id, descriptor, display_name, email)
            VALUES ($id, $desc, $name, $email)
            ON CONFLICT(id) DO UPDATE SET
                descriptor = excluded.descriptor,
                display_name = excluded.display_name,
                email = excluded.email;
            """;
        cmd.Parameters.AddWithValue("$id", identity.Id);
        cmd.Parameters.AddWithValue("$desc", identity.Descriptor);
        cmd.Parameters.AddWithValue("$name", identity.DisplayName);
        cmd.Parameters.AddWithValue("$email", (object?)identity.Email ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<CachedIdentity> GetAll()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, descriptor, display_name, email FROM identity;";
        using var reader = cmd.ExecuteReader();
        var results = new List<CachedIdentity>();
        while (reader.Read())
        {
            results.Add(new CachedIdentity(
                Id: reader.GetString(0),
                Descriptor: reader.GetString(1),
                DisplayName: reader.GetString(2),
                Email: reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return results;
    }
}
