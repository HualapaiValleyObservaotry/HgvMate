using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp.Data;

/// <summary>
/// Append-only tool usage log backed by SQLite. Writes are non-blocking via a bounded
/// <see cref="Channel{T}"/>; a background consumer flushes entries to the database.
/// </summary>
public sealed class ToolUsageLogger : IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger<ToolUsageLogger> _logger;
    private readonly Channel<ToolUsageEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<TaskCompletionSource> _flushRequests = new();
    private Task? _consumerTask;

    /// <summary>Number of days to retain usage records (auto-pruned on startup).</summary>
    internal const int RetentionDays = 90;

    public ToolUsageLogger(string dataPath, ILogger<ToolUsageLogger> logger)
    {
        _dbPath = Path.Combine(dataPath, "usage.db");
        _logger = logger;
        _channel = Channel.CreateBounded<ToolUsageEntry>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });
    }

    // ── Lifecycle ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates the schema, prunes old rows, and starts the background consumer.
    /// Called during warmup.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await EnsureSchemaAsync(connection);
        await PruneOldRecordsAsync(connection);
        _consumerTask = Task.Run(() => ConsumeAsync(_cts.Token), cancellationToken);
    }

    /// <summary>
    /// Enqueues a tool usage entry for background persistence. Non-blocking; drops if the
    /// channel is full.
    /// </summary>
    public void Log(string toolName, object? parameters, double durationMs,
                    int? resultCount = null, string? sessionId = null,
                    string? callerId = null, string? error = null)
    {
        var entry = new ToolUsageEntry
        {
            Timestamp = DateTime.UtcNow,
            ToolName = toolName,
            Parameters = parameters is not null ? JsonSerializer.Serialize(parameters) : "{}",
            DurationMs = durationMs,
            ResultCount = resultCount,
            SessionId = sessionId,
            CallerId = callerId,
            Error = error
        };

        _channel.Writer.TryWrite(entry);
    }

    // ── Analytics queries ───────────────────────────────────────────────

    /// <summary>Top tools by call count and average duration, with optional date range.</summary>
    public async Task<IReadOnlyList<ToolSummary>> GetToolSummariesAsync(
        DateTime? from = null, DateTime? to = null)
    {
        await using var connection = OpenConnection();
        var sql = """
            SELECT tool_name,
                   COUNT(*) AS calls,
                   AVG(duration_ms) AS avg_ms,
                   MAX(duration_ms) AS max_ms,
                   SUM(CASE WHEN error IS NOT NULL THEN 1 ELSE 0 END) AS error_count
            FROM tool_usage
            WHERE (@from IS NULL OR timestamp >= @from)
              AND (@to IS NULL OR timestamp <= @to)
            GROUP BY tool_name
            ORDER BY calls DESC
            """;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@from", (object?)from?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@to", (object?)to?.ToString("o") ?? DBNull.Value);

        var results = new List<ToolSummary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new ToolSummary(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetInt32(4)));
        }
        return results;
    }

    /// <summary>Finds sessions that searched the same query across multiple repos.</summary>
    public async Task<IReadOnlyList<RepeatedSearch>> GetRepeatedSearchesAsync(
        DateTime? from = null, DateTime? to = null)
    {
        await using var connection = OpenConnection();
        var sql = """
            SELECT session_id,
                   json_extract(parameters, '$.query') AS query,
                   COUNT(DISTINCT json_extract(parameters, '$.repository')) AS repo_count
            FROM tool_usage
            WHERE tool_name = 'hgvmate_search_source_code'
              AND session_id IS NOT NULL
              AND (@from IS NULL OR timestamp >= @from)
              AND (@to IS NULL OR timestamp <= @to)
            GROUP BY session_id, query
            HAVING repo_count > 1
            ORDER BY repo_count DESC
            """;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@from", (object?)from?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@to", (object?)to?.ToString("o") ?? DBNull.Value);

        var results = new List<RepeatedSearch>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new RepeatedSearch(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2)));
        }
        return results;
    }

    /// <summary>Detects common tool call sequences (what tool follows what) within sessions.</summary>
    public async Task<IReadOnlyList<ToolSequence>> GetToolSequencesAsync(
        DateTime? from = null, DateTime? to = null)
    {
        await using var connection = OpenConnection();
        var sql = """
            SELECT a.tool_name AS from_tool, b.tool_name AS to_tool, COUNT(*) AS cnt
            FROM tool_usage a
            JOIN tool_usage b ON a.session_id = b.session_id
                             AND b.id = (SELECT MIN(c.id) FROM tool_usage c
                                         WHERE c.session_id = a.session_id AND c.id > a.id)
            WHERE a.session_id IS NOT NULL
              AND (@from IS NULL OR a.timestamp >= @from)
              AND (@to IS NULL OR a.timestamp <= @to)
            GROUP BY from_tool, to_tool
            ORDER BY cnt DESC
            """;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@from", (object?)from?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@to", (object?)to?.ToString("o") ?? DBNull.Value);

        var results = new List<ToolSequence>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new ToolSequence(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2)));
        }
        return results;
    }

    /// <summary>Error rates grouped by tool, with optional date range.</summary>
    public async Task<IReadOnlyList<ToolErrorRate>> GetErrorRatesAsync(
        DateTime? from = null, DateTime? to = null)
    {
        await using var connection = OpenConnection();
        var sql = """
            SELECT tool_name,
                   COUNT(*) AS total,
                   SUM(CASE WHEN error IS NOT NULL THEN 1 ELSE 0 END) AS errors
            FROM tool_usage
            WHERE (@from IS NULL OR timestamp >= @from)
              AND (@to IS NULL OR timestamp <= @to)
            GROUP BY tool_name
            ORDER BY errors DESC
            """;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@from", (object?)from?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@to", (object?)to?.ToString("o") ?? DBNull.Value);

        var results = new List<ToolErrorRate>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var total = reader.GetInt32(1);
            var errors = reader.GetInt32(2);
            results.Add(new ToolErrorRate(
                reader.GetString(0),
                total,
                errors,
                total > 0 ? (double)errors / total * 100 : 0));
        }
        return results;
    }

    // ── Internal helpers ────────────────────────────────────────────────

    internal SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    internal static async Task EnsureSchemaAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS tool_usage (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                tool_name TEXT NOT NULL,
                parameters TEXT NOT NULL,
                duration_ms REAL NOT NULL,
                result_count INTEGER,
                session_id TEXT,
                caller_id TEXT,
                error TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_tool_usage_timestamp ON tool_usage(timestamp);
            CREATE INDEX IF NOT EXISTS idx_tool_usage_tool_name ON tool_usage(tool_name);
            CREATE INDEX IF NOT EXISTS idx_tool_usage_session   ON tool_usage(session_id);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    internal async Task PruneOldRecordsAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM tool_usage WHERE timestamp < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff",
            DateTime.UtcNow.AddDays(-RetentionDays).ToString("o"));
        var deleted = await cmd.ExecuteNonQueryAsync();
        if (deleted > 0)
            _logger.LogInformation("ToolUsageLogger: pruned {Count} records older than {Days} days.", deleted, RetentionDays);
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        try
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                var batch = new List<ToolUsageEntry>();
                while (_channel.Reader.TryRead(out var entry))
                    batch.Add(entry);

                if (batch.Count > 0)
                {
                    try
                    {
                        await WriteBatchAsync(connection, batch);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ToolUsageLogger: failed to persist {Count} entries.", batch.Count);
                    }
                }

                CompletePendingFlushes();
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }

        CompletePendingFlushes();
    }

    private static async Task WriteBatchAsync(SqliteConnection connection, List<ToolUsageEntry> entries)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tool_usage (timestamp, tool_name, parameters, duration_ms, result_count, session_id, caller_id, error)
            VALUES (@timestamp, @toolName, @parameters, @durationMs, @resultCount, @sessionId, @callerId, @error)
            """;
        var pTimestamp = cmd.Parameters.Add("@timestamp", SqliteType.Text);
        var pToolName = cmd.Parameters.Add("@toolName", SqliteType.Text);
        var pParameters = cmd.Parameters.Add("@parameters", SqliteType.Text);
        var pDurationMs = cmd.Parameters.Add("@durationMs", SqliteType.Real);
        var pResultCount = cmd.Parameters.Add("@resultCount", SqliteType.Integer);
        var pSessionId = cmd.Parameters.Add("@sessionId", SqliteType.Text);
        var pCallerId = cmd.Parameters.Add("@callerId", SqliteType.Text);
        var pError = cmd.Parameters.Add("@error", SqliteType.Text);

        foreach (var entry in entries)
        {
            pTimestamp.Value = entry.Timestamp.ToString("o");
            pToolName.Value = entry.ToolName;
            pParameters.Value = entry.Parameters;
            pDurationMs.Value = entry.DurationMs;
            pResultCount.Value = (object?)entry.ResultCount ?? DBNull.Value;
            pSessionId.Value = (object?)entry.SessionId ?? DBNull.Value;
            pCallerId.Value = (object?)entry.CallerId ?? DBNull.Value;
            pError.Value = (object?)entry.Error ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>
    /// Waits for all currently queued entries to be persisted without stopping the logger.
    /// </summary>
    public Task FlushAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _flushRequests.Enqueue(tcs);
        return tcs.Task;
    }

    private void CompletePendingFlushes()
    {
        while (_flushRequests.TryDequeue(out var tcs))
            tcs.TrySetResult();
    }

    public void Dispose()
    {
        try
        {
            _channel.Writer.TryComplete();
            _consumerTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { /* expected if the consumer was cancelled externally */ }
        catch (ObjectDisposedException) { /* expected if disposal is racing with shutdown */ }
        finally
        {
            _cts.Dispose();
        }
    }
}

// ── DTOs ────────────────────────────────────────────────────────────────────

internal sealed class ToolUsageEntry
{
    public required DateTime Timestamp { get; init; }
    public required string ToolName { get; init; }
    public required string Parameters { get; init; }
    public required double DurationMs { get; init; }
    public int? ResultCount { get; init; }
    public string? SessionId { get; init; }
    public string? CallerId { get; init; }
    public string? Error { get; init; }
}

public record ToolSummary(string ToolName, int Calls, double AvgDurationMs, double MaxDurationMs, int ErrorCount);
public record RepeatedSearch(string SessionId, string? Query, int RepoCount);
public record ToolSequence(string FromTool, string ToTool, int Count);
public record ToolErrorRate(string ToolName, int TotalCalls, int Errors, double ErrorPercent);
