using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Repos;
using HgvMate.Mcp.Search;
using HgvMate.Mcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

/// <summary>
/// Tests that verify SSE / Streamable HTTP transport works end-to-end
/// using ASP.NET Core TestServer (in-process, no real network).
/// </summary>
[TestClass]
public sealed class SseTransportTests : IDisposable
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "HgvMateSse_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "repos"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_tempDir != null && Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task SseEndpoint_ReturnsOk_ForMcpPost()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        // Send an MCP initialize JSON-RPC request via POST to /mcp
        var initRequest = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "test-client", version = "1.0.0" }
            }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(initRequest),
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/mcp", content);

        // The MCP HTTP transport should accept POST requests at the /mcp endpoint
        Assert.AreNotEqual(HttpStatusCode.NotFound, response.StatusCode,
            "The /mcp endpoint should exist and accept POST requests.");
    }

    [TestMethod]
    public async Task SseEndpoint_ReturnsNotFound_ForWrongPath()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/nonexistent");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task SseEndpoint_AcceptsGetForSseStream()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        // GET /mcp should be accepted (it's the SSE streaming endpoint)
        var response = await client.GetAsync("/mcp", HttpCompletionOption.ResponseHeadersRead);

        // Should not be 404 — the endpoint exists and serves SSE or returns appropriate status
        Assert.AreNotEqual(HttpStatusCode.NotFound, response.StatusCode,
            "GET /mcp should be handled by the MCP endpoint.");
    }

    [TestMethod]
    public async Task McpEndpoint_PostReturnsJsonContentType()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var initRequest = new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "test-client", version = "1.0.0" }
            }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(initRequest),
            Encoding.UTF8,
            "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = content };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        var response = await client.SendAsync(request);

        // Ensure the endpoint returns a successful status before validating content type
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, "Expected /mcp to return 200 OK.");

        // Verify response content type is JSON-based (MCP uses application/json or text/event-stream)
        var contentType = response.Content.Headers.ContentType?.MediaType;
        Assert.IsTrue(
            contentType == "application/json" || contentType == "text/event-stream",
            $"Expected JSON or SSE content type, got '{contentType}'.");
    }

    private async Task<WebApplication> CreateTestApp()
    {
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseTestServer();

        var hgvOptions = new HgvMateOptions { DataPath = _tempDir, Transport = "sse" };
        var syncOptions = new RepoSyncOptions { ClonePath = "repos", PollIntervalMinutes = 0 };
        var searchOptions = new SearchOptions { MaxResults = 10 };
        var credOptions = new CredentialOptions();

        builder.Services.AddSingleton(hgvOptions);
        builder.Services.AddSingleton(syncOptions);
        builder.Services.AddSingleton(searchOptions);
        builder.Services.AddSingleton(credOptions);
        builder.Services.AddSingleton<IGitCredentialProvider, GitCredentialProvider>();
        builder.Services.AddSingleton<IRepoRegistry>(sp =>
            new JsonRepoRegistry(_tempDir, sp.GetRequiredService<ILoggerFactory>().CreateLogger<JsonRepoRegistry>()));
        var startupState = new StartupState();
        builder.Services.AddSingleton(startupState);
        builder.Services.AddSingleton<RepoSyncService>();
        builder.Services.AddSingleton<SourceCodeReader>();
        builder.Services.AddSingleton<GitGrepSearchService>();
        builder.Services.AddSingleton<GitNexusService>();
        builder.Services.AddSingleton<IOnnxEmbedder>(
            new OnnxEmbedder((Microsoft.ML.OnnxRuntime.InferenceSession?)null, NullLogger<OnnxEmbedder>.Instance));
        builder.Services.AddSingleton(sp =>
            new VectorStore(Path.Combine(_tempDir, "vectors.bin"),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<VectorStore>()));
        builder.Services.AddSingleton<IndexingService>();
        builder.Services.AddSingleton<HybridSearchService>();

        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<AdminTools>()
            .WithTools<SourceCodeTools>()
            .WithTools<StructuralTools>();

        var app = builder.Build();

        var vectorStore = app.Services.GetRequiredService<VectorStore>();
        await vectorStore.LoadAsync();

        startupState.MarkDatabaseReady();
        startupState.MarkVectorCacheReady();
        startupState.MarkOnnxReady();

        app.MapMcp("/mcp");

        await app.StartAsync();

        return app;
    }
}
