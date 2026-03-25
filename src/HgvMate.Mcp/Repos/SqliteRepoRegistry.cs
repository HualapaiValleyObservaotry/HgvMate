using HgvMate.Mcp.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp.Repos;

public class SqliteRepoRegistry : IRepoRegistry
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ILogger<SqliteRepoRegistry> _logger;

    public SqliteRepoRegistry(ISqliteConnectionFactory connectionFactory, ILogger<SqliteRepoRegistry> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<RepoRecord> AddAsync(string name, string url, string branch, string source, string? addedBy = null)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        const string sql = """
            INSERT INTO repositories (name, url, branch, source, enabled, added_by, sync_state)
            VALUES (@name, @url, @branch, @source, 1, @addedBy, 'pending')
            RETURNING id, name, url, branch, source, enabled, last_sha, last_synced, added_by,
                      last_error, last_error_at, failed_sync_count, sync_state;
            """;
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@url", url);
        cmd.Parameters.AddWithValue("@branch", branch);
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@addedBy", (object?)addedBy ?? DBNull.Value);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return MapRecord(reader);
        throw new InvalidOperationException($"Failed to insert repository '{name}'.");
    }

    public async Task<bool> RemoveAsync(string name)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        const string sql = "DELETE FROM repositories WHERE name = @name;";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", name);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<IReadOnlyList<RepoRecord>> GetAllAsync()
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        const string sql = """
            SELECT id, name, url, branch, source, enabled, last_sha, last_synced, added_by,
                   last_error, last_error_at, failed_sync_count, sync_state
            FROM repositories ORDER BY name;
            """;
        using var cmd = new SqliteCommand(sql, conn);
        using var reader = await cmd.ExecuteReaderAsync();
        var results = new List<RepoRecord>();
        while (await reader.ReadAsync())
            results.Add(MapRecord(reader));
        return results;
    }

    public async Task<RepoRecord?> GetByNameAsync(string name)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        const string sql = """
            SELECT id, name, url, branch, source, enabled, last_sha, last_synced, added_by,
                   last_error, last_error_at, failed_sync_count, sync_state
            FROM repositories WHERE name = @name;
            """;
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", name);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return MapRecord(reader);
        return null;
    }

    public async Task<RepoRecord?> GetByUrlAsync(string url)
    {
        var normalized = NormalizeUrl(url);
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        const string sql = """
            SELECT id, name, url, branch, source, enabled, last_sha, last_synced, added_by,
                   last_error, last_error_at, failed_sync_count, sync_state
            FROM repositories;
            """;
        using var cmd = new SqliteCommand(sql, conn);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var record = MapRecord(reader);
            if (NormalizeUrl(record.Url) == normalized)
                return record;
        }
        return null;
    }

    internal static string NormalizeUrl(string url)
    {
        var normalized = url.Trim().ToLowerInvariant();
        // Strip trailing .git
        if (normalized.EndsWith(".git"))
            normalized = normalized[..^4];
        // Strip trailing slashes
        normalized = normalized.TrimEnd('/');
        // Normalize protocol — treat https://github.com and http://github.com the same
        normalized = normalized.Replace("http://", "https://");
        // Strip embedded credentials (https://user:pass@host → https://host)
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            normalized = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".TrimEnd('/');
        return normalized;
    }

    public async Task<bool> UpdateLastShaAsync(string name, string sha)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        const string sql = "UPDATE repositories SET last_sha = @sha WHERE name = @name;";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@sha", sha);
        cmd.Parameters.AddWithValue("@name", name);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> UpdateLastSyncedAsync(string name, DateTime syncedAt)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        const string sql = "UPDATE repositories SET last_synced = @syncedAt WHERE name = @name;";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@syncedAt", syncedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@name", name);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> SetEnabledAsync(string name, bool enabled)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        const string sql = "UPDATE repositories SET enabled = @enabled WHERE name = @name;";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@name", name);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> UpdateSyncStateAsync(string name, string state)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        const string sql = "UPDATE repositories SET sync_state = @state WHERE name = @name;";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@state", state);
        cmd.Parameters.AddWithValue("@name", name);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> UpdateSyncErrorAsync(string name, string error)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        const string sql = """
            UPDATE repositories
            SET sync_state = 'failed',
                last_error = @error,
                last_error_at = @errorAt,
                failed_sync_count = failed_sync_count + 1
            WHERE name = @name;
            """;
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@error", error);
        cmd.Parameters.AddWithValue("@errorAt", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@name", name);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> ClearSyncErrorAsync(string name)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        const string sql = """
            UPDATE repositories
            SET sync_state = 'synced',
                last_error = NULL,
                last_error_at = NULL,
                failed_sync_count = 0
            WHERE name = @name;
            """;
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", name);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    private static RepoRecord MapRecord(SqliteDataReader reader) => new(
        Id: reader.GetInt32(0),
        Name: reader.GetString(1),
        Url: reader.GetString(2),
        Branch: reader.GetString(3),
        Source: reader.GetString(4),
        Enabled: reader.GetInt32(5) != 0,
        LastSha: reader.IsDBNull(6) ? null : reader.GetString(6),
        LastSynced: reader.IsDBNull(7) ? null : reader.GetString(7),
        AddedBy: reader.IsDBNull(8) ? null : reader.GetString(8),
        LastError: reader.IsDBNull(9) ? null : reader.GetString(9),
        LastErrorAt: reader.IsDBNull(10) ? null : reader.GetString(10),
        FailedSyncCount: reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
        SyncState: reader.IsDBNull(12) ? SyncStates.Pending : reader.GetString(12)
    );
}
