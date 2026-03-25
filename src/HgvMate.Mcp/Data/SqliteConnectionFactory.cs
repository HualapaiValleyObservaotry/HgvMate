using System.Data.Common;
using HVO.Enterprise.Telemetry.Data.AdoNet.Extensions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp.Data;

public interface ISqliteConnectionFactory
{
    SqliteConnection CreateConnection();
    DbConnection CreateInstrumentedConnection() => CreateConnection();
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

    public DbConnection CreateInstrumentedConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        return connection.WithTelemetry();
    }
}
