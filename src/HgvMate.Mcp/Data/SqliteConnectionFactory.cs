using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp.Data;

public interface ISqliteConnectionFactory
{
    SqliteConnection CreateConnection();
}

public class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteConnectionFactory> _logger;

    public SqliteConnectionFactory(string connectionString, ILogger<SqliteConnectionFactory> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }
}
