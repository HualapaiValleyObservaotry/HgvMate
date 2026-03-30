using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Repos;
using HgvMate.Mcp.Search;
using HgvMate.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

[TestClass]
public sealed class ServerInfoToolsTests
{
    private FakeRepoRegistry _registry = null!;
    private IOnnxEmbedder _embedder = null!;
    private HgvMateOptions _options = null!;
    private ServerInfoTools _tools = null!;

    [TestInitialize]
    public void Setup()
    {
        _registry = new FakeRepoRegistry();
        _embedder = new OnnxEmbedder(
            (Microsoft.ML.OnnxRuntime.InferenceSession?)null,
            NullLogger<OnnxEmbedder>.Instance);
        _options = new HgvMateOptions { Transport = "sse" };
        _tools = new ServerInfoTools(_registry, _embedder, _options);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ServerInfo_ReturnsVersionInfo()
    {
        var result = await _tools.ServerInfo();

        StringAssert.Contains(result, "HgvMate Server Info");
        StringAssert.Contains(result, "**Version:**");
        StringAssert.Contains(result, "**Git SHA:**");
        StringAssert.Contains(result, "**Build Date:**");
        StringAssert.Contains(result, "**Uptime:**");
        StringAssert.Contains(result, "**Transport:** sse");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ServerInfo_ReportsCapabilities()
    {
        var result = await _tools.ServerInfo();

        StringAssert.Contains(result, "Capabilities");
        StringAssert.Contains(result, "**Repository Count:** 0");
        StringAssert.Contains(result, "all-MiniLM-L6-v2");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ServerInfo_ReportsEndpoints()
    {
        var result = await _tools.ServerInfo();

        StringAssert.Contains(result, "/health");
        StringAssert.Contains(result, "/api/*");
        StringAssert.Contains(result, "/mcp");
        StringAssert.Contains(result, "/diagnostics");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ServerInfo_IncludesRepoCount()
    {
        await _registry.AddAsync("repo1", "https://example.com/repo1", "main", "github");
        await _registry.AddAsync("repo2", "https://example.com/repo2", "main", "github");

        var result = await _tools.ServerInfo();

        StringAssert.Contains(result, "**Repository Count:** 2");
    }
}
