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

        var sql = """
            CREATE TABLE IF NOT EXISTS repositories (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                url TEXT NOT NULL,
                branch TEXT NOT NULL DEFAULT 'main',
                source TEXT NOT NULL DEFAULT 'github',
                enabled INTEGER NOT NULL DEFAULT 1,
                last_sha TEXT,
                last_synced TEXT,
                added_by TEXT
            );
            """;

        using var cmd = new SqliteCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
        _logger.LogInformation("Database schema initialized.");
    }
}
