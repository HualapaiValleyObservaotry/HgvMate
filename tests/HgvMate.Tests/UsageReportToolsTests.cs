using HgvMate.Mcp.Data;
using HgvMate.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class UsageReportToolsTests : IDisposable
{
    private string _tempDir = null!;
    private ToolUsageLogger _usageLogger = null!;
    private UsageReportTools _tools = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "HgvMateReport_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _usageLogger = new ToolUsageLogger(_tempDir, NullLogger<ToolUsageLogger>.Instance);
        await _usageLogger.InitializeAsync();
        _tools = new UsageReportTools(_usageLogger);
    }

    [TestCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _usageLogger?.Dispose();
        if (_tempDir != null && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (IOException) { /* timing-dependent cleanup */ }
        }
    }

    [TestMethod]
    public async Task UsageReport_Summary_NoData_ReturnsNoUsageMessage()
    {
        var result = await _tools.UsageReport("summary");
        StringAssert.Contains(result, "No usage data");
    }

    [TestMethod]
    public async Task UsageReport_Summary_WithData_ReturnsToolStats()
    {
        _usageLogger.Log("hgvmate_search_source_code", null, 10.0);
        _usageLogger.Log("hgvmate_search_source_code", null, 20.0);
        await _usageLogger.FlushAsync();

        // Re-create to get a fresh logger that can read
        _usageLogger = new ToolUsageLogger(_tempDir, NullLogger<ToolUsageLogger>.Instance);
        await _usageLogger.InitializeAsync();
        _tools = new UsageReportTools(_usageLogger);

        var result = await _tools.UsageReport("summary");
        StringAssert.Contains(result, "hgvmate_search_source_code");
        StringAssert.Contains(result, "Tool Usage Summary");
    }

    [TestMethod]
    public async Task UsageReport_InvalidReport_ReturnsError()
    {
        var result = await _tools.UsageReport("invalid");
        StringAssert.Contains(result, "Error");
        StringAssert.Contains(result, "report must be one of");
    }

    [TestMethod]
    public async Task UsageReport_InvalidFromDate_ReturnsError()
    {
        var result = await _tools.UsageReport(from: "not-a-date");
        StringAssert.Contains(result, "Error");
        StringAssert.Contains(result, "valid ISO 8601");
    }

    [TestMethod]
    public async Task UsageReport_InvalidToDate_ReturnsError()
    {
        var result = await _tools.UsageReport(to: "not-a-date");
        StringAssert.Contains(result, "Error");
        StringAssert.Contains(result, "valid ISO 8601");
    }

    [TestMethod]
    public async Task UsageReport_All_IncludesAllSections()
    {
        var result = await _tools.UsageReport("all");
        StringAssert.Contains(result, "Tool Usage Summary");
        StringAssert.Contains(result, "Repeated Cross-Repo Searches");
        StringAssert.Contains(result, "Common Tool Sequences");
        StringAssert.Contains(result, "Error Rates by Tool");
    }

    [TestMethod]
    public async Task UsageReport_Errors_WithData_ShowsErrorRates()
    {
        _usageLogger.Log("flaky_tool", null, 5.0, error: "fail");
        _usageLogger.Log("flaky_tool", null, 5.0);
        await _usageLogger.FlushAsync();

        _usageLogger = new ToolUsageLogger(_tempDir, NullLogger<ToolUsageLogger>.Instance);
        await _usageLogger.InitializeAsync();
        _tools = new UsageReportTools(_usageLogger);

        var result = await _tools.UsageReport("errors");
        StringAssert.Contains(result, "flaky_tool");
        StringAssert.Contains(result, "Error Rates by Tool");
    }

    [TestMethod]
    public async Task UsageReport_RepeatedSearches_NoData_ReturnsNoRepeats()
    {
        var result = await _tools.UsageReport("repeated_searches");
        StringAssert.Contains(result, "No repeated cross-repo searches");
    }

    [TestMethod]
    public async Task UsageReport_Sequences_NoData_ReturnsNoSequences()
    {
        var result = await _tools.UsageReport("sequences");
        StringAssert.Contains(result, "No tool sequences");
    }
}
