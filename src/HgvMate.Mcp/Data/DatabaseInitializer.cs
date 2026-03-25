using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp.Data;

public class DatabaseInitializer
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(ISqliteConnectionFactory connectionFactory, ILogger<DatabaseInitializer> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing database schema...");
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();

        var createSql = """
            CREATE TABLE IF NOT EXISTS repositories (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                url TEXT NOT NULL,
                branch TEXT NOT NULL DEFAULT 'main',
                source TEXT NOT NULL DEFAULT 'github',
                enabled INTEGER NOT NULL DEFAULT 1,
                last_sha TEXT,
                last_synced TEXT,
                added_by TEXT,
                last_error TEXT,
                last_error_at TEXT,
                failed_sync_count INTEGER NOT NULL DEFAULT 0,
                sync_state TEXT NOT NULL DEFAULT 'pending'
            );
            """;

        using var createCmd = new SqliteCommand(createSql, conn);
        await createCmd.ExecuteNonQueryAsync();

        // Migrate existing databases that predate the error-tracking columns
        await AddColumnIfMissingAsync(conn, "last_error", "TEXT");
        await AddColumnIfMissingAsync(conn, "last_error_at", "TEXT");
        await AddColumnIfMissingAsync(conn, "failed_sync_count", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(conn, "sync_state", "TEXT NOT NULL DEFAULT 'pending'");

        _logger.LogInformation("Database schema initialized.");
    }

    private static async Task AddColumnIfMissingAsync(SqliteConnection conn, string columnName, string columnDef)
    {
        try
        {
            var sql = $"ALTER TABLE repositories ADD COLUMN {columnName} {columnDef};";
            using var cmd = new SqliteCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Column already exists — this is the expected case for existing databases
        }
    }
}
