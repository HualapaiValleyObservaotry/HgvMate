using HgvMate.Mcp.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ToolUsageLoggerTests : IDisposable
{
    private string _tempDir = null!;
    private ToolUsageLogger _logger = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "HgvMateUsage_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _logger = new ToolUsageLogger(_tempDir, NullLogger<ToolUsageLogger>.Instance);
    }

    [TestCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _logger?.Dispose();
        if (_tempDir != null && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (IOException) { /* timing-dependent cleanup may fail */ }
        }
    }

    // ── Schema ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task InitializeAsync_CreatesDatabase()
    {
        await _logger.InitializeAsync();

        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "usage.db")));
    }

    [TestMethod]
    public async Task InitializeAsync_CreatesSchemaWithExpectedColumns()
    {
        await _logger.InitializeAsync();

        await using var conn = _logger.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(tool_usage)";
        await using var reader = await cmd.ExecuteReaderAsync();

        var columns = new List<string>();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));

        CollectionAssert.Contains(columns, "id");
        CollectionAssert.Contains(columns, "timestamp");
        CollectionAssert.Contains(columns, "tool_name");
        CollectionAssert.Contains(columns, "parameters");
        CollectionAssert.Contains(columns, "duration_ms");
        CollectionAssert.Contains(columns, "result_count");
        CollectionAssert.Contains(columns, "session_id");
        CollectionAssert.Contains(columns, "caller_id");
        CollectionAssert.Contains(columns, "error");
    }

    [TestMethod]
    public async Task InitializeAsync_CreatesExpectedIndexes()
    {
        await _logger.InitializeAsync();

        await using var conn = _logger.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='tool_usage'";
        await using var reader = await cmd.ExecuteReaderAsync();

        var indexes = new List<string>();
        while (await reader.ReadAsync())
            indexes.Add(reader.GetString(0));

        CollectionAssert.Contains(indexes, "idx_tool_usage_timestamp");
        CollectionAssert.Contains(indexes, "idx_tool_usage_tool_name");
        CollectionAssert.Contains(indexes, "idx_tool_usage_session");
    }

    [TestMethod]
    public async Task InitializeAsync_IdempotentWhenCalledTwice()
    {
        await _logger.InitializeAsync();
        // Should not throw when called a second time
        _logger.Dispose();
        _logger = new ToolUsageLogger(_tempDir, NullLogger<ToolUsageLogger>.Instance);
        await _logger.InitializeAsync();

        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "usage.db")));
    }

    // ── Write + Flush ───────────────────────────────────────────────────

    [TestMethod]
    public async Task Log_WritesEntryToDatabase()
    {
        await _logger.InitializeAsync();

        _logger.Log("test_tool", new { query = "hello" }, 42.5, resultCount: 3, sessionId: "s1", callerId: "c1");
        await _logger.FlushAsync();

        await using var conn = _logger.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tool_name, parameters, duration_ms, result_count, session_id, caller_id, error FROM tool_usage";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync(), "Expected at least one row");
        Assert.AreEqual("test_tool", reader.GetString(0));
        StringAssert.Contains(reader.GetString(1), "hello");
        Assert.AreEqual(42.5, reader.GetDouble(2), 0.01);
        Assert.AreEqual(3, reader.GetInt32(3));
        Assert.AreEqual("s1", reader.GetString(4));
        Assert.AreEqual("c1", reader.GetString(5));
        Assert.IsTrue(reader.IsDBNull(6), "error should be null");
    }

    [TestMethod]
    public async Task Log_WithError_StoresErrorMessage()
    {
        await _logger.InitializeAsync();

        _logger.Log("failing_tool", null, 10.0, error: "something broke");
        await _logger.FlushAsync();

        await using var conn = _logger.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT error FROM tool_usage WHERE tool_name='failing_tool'";
        var error = await cmd.ExecuteScalarAsync();
        Assert.AreEqual("something broke", error);
    }

    [TestMethod]
    public async Task Log_NullParameters_StoresEmptyJson()
    {
        await _logger.InitializeAsync();

        _logger.Log("null_params_tool", null, 5.0);
        await _logger.FlushAsync();

        await using var conn = _logger.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT parameters FROM tool_usage WHERE tool_name='null_params_tool'";
        var parameters = await cmd.ExecuteScalarAsync();
        Assert.AreEqual("{}", parameters);
    }

    [TestMethod]
    public async Task Log_MultipleEntries_AllPersisted()
    {
        await _logger.InitializeAsync();

        for (int i = 0; i < 10; i++)
            _logger.Log($"tool_{i}", new { index = i }, i * 10.0);
        await _logger.FlushAsync();

        await using var conn = _logger.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tool_usage";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.AreEqual(10, count);
    }

    // ── Auto-Prune ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task PruneOldRecords_RemovesRecordsOlderThanRetentionDays()
    {
        await _logger.InitializeAsync();

        // Insert an old record directly into the database
        await using var conn = _logger.OpenConnection();
        await using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO tool_usage (timestamp, tool_name, parameters, duration_ms)
            VALUES (@ts, 'old_tool', '{}', 1.0)
            """;
        insertCmd.Parameters.AddWithValue("@ts",
            DateTime.UtcNow.AddDays(-(ToolUsageLogger.RetentionDays + 1)).ToString("o"));
        await insertCmd.ExecuteNonQueryAsync();

        // Insert a recent record
        await using var insertCmd2 = conn.CreateCommand();
        insertCmd2.CommandText = """
            INSERT INTO tool_usage (timestamp, tool_name, parameters, duration_ms)
            VALUES (@ts, 'new_tool', '{}', 2.0)
            """;
        insertCmd2.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
        await insertCmd2.ExecuteNonQueryAsync();

        // Verify we have 2 records
        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM tool_usage";
        Assert.AreEqual(2, Convert.ToInt32(await countCmd.ExecuteScalarAsync()));

        // Run prune
        await _logger.PruneOldRecordsAsync(conn);

        // Should have only 1 record remaining (the recent one)
        await using var countCmd2 = conn.CreateCommand();
        countCmd2.CommandText = "SELECT COUNT(*) FROM tool_usage";
        Assert.AreEqual(1, Convert.ToInt32(await countCmd2.ExecuteScalarAsync()));

        await using var verifyCmd = conn.CreateCommand();
        verifyCmd.CommandText = "SELECT tool_name FROM tool_usage";
        Assert.AreEqual("new_tool", await verifyCmd.ExecuteScalarAsync());
    }

    // ── Analytics: Tool Summaries ────────────────────────────────────────

    [TestMethod]
    public async Task GetToolSummariesAsync_ReturnsCorrectAggregates()
    {
        await _logger.InitializeAsync();

        _logger.Log("tool_a", null, 10.0);
        _logger.Log("tool_a", null, 20.0);
        _logger.Log("tool_a", null, 30.0, error: "fail");
        _logger.Log("tool_b", null, 50.0);
        await _logger.FlushAsync();

        var summaries = await _logger.GetToolSummariesAsync();

        Assert.AreEqual(2, summaries.Count);

        var toolA = summaries.First(s => s.ToolName == "tool_a");
        Assert.AreEqual(3, toolA.Calls);
        Assert.AreEqual(20.0, toolA.AvgDurationMs, 0.01);
        Assert.AreEqual(30.0, toolA.MaxDurationMs, 0.01);
        Assert.AreEqual(1, toolA.ErrorCount);

        var toolB = summaries.First(s => s.ToolName == "tool_b");
        Assert.AreEqual(1, toolB.Calls);
        Assert.AreEqual(0, toolB.ErrorCount);
    }

    [TestMethod]
    public async Task GetToolSummariesAsync_EmptyTable_ReturnsEmptyList()
    {
        await _logger.InitializeAsync();

        var summaries = await _logger.GetToolSummariesAsync();

        Assert.AreEqual(0, summaries.Count);
    }

    // ── Analytics: Repeated Searches ────────────────────────────────────

    [TestMethod]
    public async Task GetRepeatedSearchesAsync_DetectsCrossRepoSearches()
    {
        await _logger.InitializeAsync();

        // Same session, same query, different repos
        InsertSearchDirectly("session1", "hello world", "repo_a");
        InsertSearchDirectly("session1", "hello world", "repo_b");
        InsertSearchDirectly("session1", "hello world", "repo_c");

        // Different session, same query — should not match (only 1 repo)
        InsertSearchDirectly("session2", "hello world", "repo_a");

        var repeated = await _logger.GetRepeatedSearchesAsync();

        Assert.AreEqual(1, repeated.Count);
        Assert.AreEqual("session1", repeated[0].SessionId);
        Assert.AreEqual("hello world", repeated[0].Query);
        Assert.AreEqual(3, repeated[0].RepoCount);
    }

    [TestMethod]
    public async Task GetRepeatedSearchesAsync_NoRepeats_ReturnsEmpty()
    {
        await _logger.InitializeAsync();

        InsertSearchDirectly("session1", "hello", "repo_a");
        InsertSearchDirectly("session2", "world", "repo_b");

        var repeated = await _logger.GetRepeatedSearchesAsync();
        Assert.AreEqual(0, repeated.Count);
    }

    // ── Analytics: Tool Sequences ───────────────────────────────────────

    [TestMethod]
    public async Task GetToolSequencesAsync_DetectsCommonSequences()
    {
        await _logger.InitializeAsync();

        // Session 1: find_symbol → get_references → get_call_chain
        InsertToolUsageDirectly("find_symbol", "s1");
        InsertToolUsageDirectly("get_references", "s1");
        InsertToolUsageDirectly("get_call_chain", "s1");

        // Session 2: find_symbol → get_references (same first two steps)
        InsertToolUsageDirectly("find_symbol", "s2");
        InsertToolUsageDirectly("get_references", "s2");

        var sequences = await _logger.GetToolSequencesAsync();

        Assert.IsTrue(sequences.Count > 0, "Should detect at least one sequence");
        var findToRef = sequences.FirstOrDefault(s => s.FromTool == "find_symbol" && s.ToTool == "get_references");
        Assert.IsNotNull(findToRef);
        Assert.AreEqual(2, findToRef.Count); // occurred in 2 sessions
    }

    // ── Analytics: Error Rates ──────────────────────────────────────────

    [TestMethod]
    public async Task GetErrorRatesAsync_CalculatesCorrectRates()
    {
        await _logger.InitializeAsync();

        _logger.Log("reliable_tool", null, 5.0);
        _logger.Log("reliable_tool", null, 5.0);
        _logger.Log("flaky_tool", null, 5.0, error: "err1");
        _logger.Log("flaky_tool", null, 5.0, error: "err2");
        _logger.Log("flaky_tool", null, 5.0);
        await _logger.FlushAsync();

        var errors = await _logger.GetErrorRatesAsync();

        var flaky = errors.First(e => e.ToolName == "flaky_tool");
        Assert.AreEqual(3, flaky.TotalCalls);
        Assert.AreEqual(2, flaky.Errors);
        Assert.AreEqual(66.67, flaky.ErrorPercent, 0.1);

        var reliable = errors.First(e => e.ToolName == "reliable_tool");
        Assert.AreEqual(0, reliable.Errors);
        Assert.AreEqual(0.0, reliable.ErrorPercent, 0.01);
    }

    // ── Date Range Filtering ────────────────────────────────────────────

    [TestMethod]
    public async Task GetToolSummariesAsync_RespectsDateRangeFilter()
    {
        await _logger.InitializeAsync();

        // Insert records at specific timestamps
        await using var conn = _logger.OpenConnection();
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("o");
        var tomorrow = DateTime.UtcNow.AddDays(1).ToString("o");

        await InsertWithTimestamp(conn, "old_tool", yesterday);
        await InsertWithTimestamp(conn, "future_tool", tomorrow);

        // Query only "tomorrow" range
        var summaries = await _logger.GetToolSummariesAsync(from: DateTime.UtcNow);
        Assert.AreEqual(1, summaries.Count);
        Assert.AreEqual("future_tool", summaries[0].ToolName);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void InsertSearchDirectly(string sessionId, string query, string repository)
    {
        using var conn = _logger.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tool_usage (tool_name, parameters, duration_ms, session_id)
            VALUES ('hgvmate_search_source_code', @params, 10.0, @sid)
            """;
        cmd.Parameters.AddWithValue("@params",
            System.Text.Json.JsonSerializer.Serialize(new { query, repository }));
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.ExecuteNonQuery();
    }

    private void InsertToolUsageDirectly(string toolName, string sessionId)
    {
        using var conn = _logger.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tool_usage (tool_name, parameters, duration_ms, session_id)
            VALUES (@tool, '{}', 5.0, @sid)
            """;
        cmd.Parameters.AddWithValue("@tool", toolName);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.ExecuteNonQuery();
    }

    private static async Task InsertWithTimestamp(SqliteConnection conn, string toolName, string timestamp)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tool_usage (timestamp, tool_name, parameters, duration_ms)
            VALUES (@ts, @tool, '{}', 1.0)
            """;
        cmd.Parameters.AddWithValue("@ts", timestamp);
        cmd.Parameters.AddWithValue("@tool", toolName);
        await cmd.ExecuteNonQueryAsync();
    }
}
