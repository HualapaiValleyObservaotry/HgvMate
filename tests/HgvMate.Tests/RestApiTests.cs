using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HgvMate.Mcp.Api;
using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Data;
using HgvMate.Mcp.Repos;
using HgvMate.Mcp.Search;
using HgvMate.Mcp.Tools;
using HVO.Enterprise.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scalar.AspNetCore;

namespace HgvMate.Tests;

[TestClass]
public sealed class RestApiTests : IDisposable
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "HgvMateApi_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "repos"));
    }

    [TestCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        if (_tempDir != null && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (IOException) { /* background sync may still hold files */ }
        }
    }

    [TestMethod]
    public async Task Health_ReturnsStartingStatus_WhenWarmupNotComplete()
    {
        await using var app = await CreateTestApp(markReady: false);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("starting", body.GetProperty("status").GetString());
        Assert.IsTrue(body.TryGetProperty("warmup", out var warmup));
        Assert.IsFalse(warmup.GetProperty("vectorCache").GetBoolean());
    }

    [TestMethod]
    public async Task Health_ReturnsStatusAndRepoDetails()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        // Add a repo so we have something to report on
        await client.PostAsJsonAsync("/api/repositories", new { name = "health-test", url = "https://github.com/example/test.git" });

        var response = await client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("healthy", body.GetProperty("status").GetString());
        Assert.IsTrue(body.TryGetProperty("uptime", out _));
        Assert.IsTrue(body.TryGetProperty("embedder", out var embedder));
        Assert.IsTrue(embedder.TryGetProperty("available", out _));
        Assert.IsTrue(body.TryGetProperty("disk", out _));
        var repos = body.GetProperty("repositories");
        Assert.AreEqual(1, repos.GetProperty("total").GetInt32());
        Assert.AreEqual(1, repos.GetProperty("details").GetArrayLength());
    }

    [TestMethod]
    public async Task ListRepositories_ReturnsEmptyArray()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/repositories");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var repos = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(JsonValueKind.Array, repos.ValueKind);
        Assert.AreEqual(0, repos.GetArrayLength());
    }

    [TestMethod]
    public async Task AddRepository_ReturnsAccepted()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/repositories", new
        {
            name = "test-repo",
            url = "https://github.com/example/test.git",
            branch = "main",
            source = "github"
        });

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("test-repo", body.GetProperty("name").GetString());
        Assert.IsTrue(body.TryGetProperty("syncState", out var syncState));
        Assert.AreEqual("pending", syncState.GetString());
    }

    [TestMethod]
    public async Task AddRepository_Duplicate_ReturnsConflict()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var repo = new { name = "dupe", url = "https://github.com/example/test.git" };
        await client.PostAsJsonAsync("/api/repositories", repo);
        var response = await client.PostAsJsonAsync("/api/repositories", repo);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    [TestMethod]
    public async Task AddRepository_DuplicateUrl_DifferentName_ReturnsConflict()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        await client.PostAsJsonAsync("/api/repositories", new { name = "repo1", url = "https://github.com/example/test.git" });
        var response = await client.PostAsJsonAsync("/api/repositories", new { name = "repo2", url = "https://github.com/example/test" });

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    [TestMethod]
    public async Task AddRepository_MissingName_ReturnsBadRequest()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/repositories", new { name = "", url = "https://x.git" });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task RemoveRepository_NotFound()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.DeleteAsync("/api/repositories/nonexistent");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task RemoveRepository_Success()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        await client.PostAsJsonAsync("/api/repositories", new { name = "to-remove", url = "https://x.git" });
        var response = await client.DeleteAsync("/api/repositories/to-remove");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task GetRepositoryStatus_NotFound()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/repositories/ghost/status");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task GetAllStatus_ReturnsOk()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/status");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task Search_MissingQuery_ReturnsBadRequest()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/search?query=");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task Search_ValidQuery_ReturnsOk()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/search?query=hello");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task OpenApiDocument_IsAvailable()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        var version = doc.GetProperty("openapi").GetString()!;
        Assert.Contains("3.1", version, "OpenAPI version should be 3.1.x.");
    }

    [TestMethod]
    public async Task ScalarUI_IsAvailable()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/scalar/v1");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("<!doctype html>", content, "Scalar UI page should serve HTML.");
    }

    [TestMethod]
    public async Task ReindexAll_ReturnsOk()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/repositories/reindex", null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task ReindexAll_Force_ReturnsOkWithForceMessage()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/repositories/reindex?force=true", null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var message = body.GetProperty("message").GetString()!;
        Assert.Contains("(force)", message, $"Expected '(force)' in message but got: {message}");
    }

    [TestMethod]
    public async Task ReindexRepo_InvalidScope_ReturnsBadRequest()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        // Add a repo first
        await client.PostAsJsonAsync("/api/repositories", new { name = "scope-repo", url = "https://github.com/example/repo.git" });

        var response = await client.PostAsync("/api/repositories/scope-repo/reindex?scope=invalid", null);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.TryGetProperty("detail", out var detail));
        Assert.Contains("scope must be one of", detail.GetString()!);
    }

    [TestMethod]
    public async Task ReindexRepo_VectorsScope_UnclonedRepo_ReturnsConflict()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        await client.PostAsJsonAsync("/api/repositories", new { name = "vec-repo", url = "https://github.com/example/vec.git" });

        var response = await client.PostAsync("/api/repositories/vec-repo/reindex?scope=vectors", null);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.TryGetProperty("detail", out var detail));
        Assert.Contains("not cloned yet", detail.GetString()!);
    }

    [TestMethod]
    public async Task ReindexRepo_GitNexusScope_UnclonedRepo_ReturnsConflict()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        await client.PostAsJsonAsync("/api/repositories", new { name = "gn-repo", url = "https://github.com/example/gn.git" });

        var response = await client.PostAsync("/api/repositories/gn-repo/reindex?scope=gitnexus", null);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.TryGetProperty("detail", out var detail));
        Assert.Contains("not cloned yet", detail.GetString()!);
    }

    [TestMethod]
    public async Task Diagnostics_ReturnsOk_WithExpectedFields()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/diagnostics");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.TryGetProperty("uptime", out _));
        Assert.IsTrue(body.TryGetProperty("queueDepth", out _));
    }

    [TestMethod]
    public async Task AddRepository_MissingName_ReturnsProblemDetails()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/repositories", new { name = "", url = "https://x.git" });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.TryGetProperty("status", out var status), "ProblemDetails must have 'status'");
        Assert.AreEqual(400, status.GetInt32());
        Assert.IsTrue(body.TryGetProperty("detail", out _), "ProblemDetails must have 'detail'");
    }

    [TestMethod]
    public async Task AddRepository_Conflict_ReturnsProblemDetails()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var repo = new { name = "conflict-repo", url = "https://github.com/example/conflict.git" };
        await client.PostAsJsonAsync("/api/repositories", repo);
        var response = await client.PostAsJsonAsync("/api/repositories", repo);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.TryGetProperty("status", out var status), "ProblemDetails must have 'status'");
        Assert.AreEqual(409, status.GetInt32());
        Assert.IsTrue(body.TryGetProperty("detail", out _), "ProblemDetails must have 'detail'");
    }

    [TestMethod]
    public async Task GetRepositoryStatus_NotFound_ReturnsProblemDetails()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/repositories/ghost/status");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.TryGetProperty("status", out var status), "ProblemDetails must have 'status'");
        Assert.AreEqual(404, status.GetInt32());
        Assert.IsTrue(body.TryGetProperty("detail", out _), "ProblemDetails must have 'detail'");
    }

    [TestMethod]
    public async Task Search_WithIncludeExtensions_ReturnsOk()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/search?query=hello&includeExtensions=.cs,.ts");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task Search_WithExcludePatterns_ReturnsOk()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/search?query=hello&excludePatterns=*.min.js,package-lock.json");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task GetRepoTree_UnknownRepo_ReturnsNotFound()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/repositories/no-such-repo/tree");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.TryGetProperty("status", out var status));
        Assert.AreEqual(404, status.GetInt32());
    }

    [TestMethod]
    public async Task GetRepoTree_InvalidDepth_ReturnsBadRequest()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/repositories/some-repo/tree?depth=0");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task FindFiles_MissingPattern_ReturnsBadRequest()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/repositories/some-repo/find?pattern=");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task FindFiles_UnknownRepo_ReturnsNotFound()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/repositories/no-such-repo/find?pattern=*.cs");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.TryGetProperty("status", out var status));
        Assert.AreEqual(404, status.GetInt32());
    }

    [TestMethod]
    public async Task GetTechStack_UnknownRepo_ReturnsNotFound()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/repositories/no-such-repo/techstack");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.TryGetProperty("status", out var status));
        Assert.AreEqual(404, status.GetInt32());
    }

    [TestMethod]
    public async Task ServerInfo_ReturnsVersionAndCapabilities()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/server-info");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.TryGetProperty("name", out var name));
        Assert.AreEqual("HgvMate", name.GetString());
        Assert.IsTrue(body.TryGetProperty("version", out _));
        Assert.IsTrue(body.TryGetProperty("gitSha", out _));
        Assert.IsTrue(body.TryGetProperty("buildDate", out _));
        Assert.IsTrue(body.TryGetProperty("uptime", out _));
        Assert.IsTrue(body.TryGetProperty("capabilities", out var caps));
        Assert.IsTrue(caps.TryGetProperty("vectorSearch", out _));
        Assert.IsTrue(caps.TryGetProperty("structuralAnalysis", out _));
        Assert.IsTrue(body.TryGetProperty("endpoints", out _));
    }

    [TestMethod]
    public async Task Health_IncludesVersionInfo()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.TryGetProperty("version", out _));
        Assert.IsTrue(body.TryGetProperty("gitSha", out _));
        Assert.IsTrue(body.TryGetProperty("buildDate", out _));
    }

    [TestMethod]
    public async Task Diagnostics_ReturnsAtBothPaths()
    {
        await using var app = await CreateTestApp();
        var client = app.GetTestClient();

        var response1 = await client.GetAsync("/diagnostics");
        Assert.AreEqual(HttpStatusCode.OK, response1.StatusCode);
        var body1 = await response1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body1.TryGetProperty("uptime", out _));

        var response2 = await client.GetAsync("/api/diagnostics");
        Assert.AreEqual(HttpStatusCode.OK, response2.StatusCode);
        var body2 = await response2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body2.TryGetProperty("uptime", out _));
    }

    // ── Test app factory ────────────────────────────────────────────────

    private async Task<WebApplication> CreateTestApp(bool markReady = true)
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
        builder.Services.AddSingleton(sp =>
            new ToolUsageLogger(_tempDir, sp.GetRequiredService<ILoggerFactory>().CreateLogger<ToolUsageLogger>()));

        builder.Services.AddTelemetry(options =>
        {
            options.ServiceName = "HgvMate.Tests";
            options.Enabled = true;
        });

        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails();

        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<AdminTools>()
            .WithTools<SourceCodeTools>()
            .WithTools<StructuralTools>()
            .WithTools<UsageReportTools>()
            .WithTools<ServerInfoTools>();

        var app = builder.Build();

        var vectorStore = app.Services.GetRequiredService<VectorStore>();
        await vectorStore.LoadAsync();

        var usageLogger = app.Services.GetRequiredService<ToolUsageLogger>();
        await usageLogger.InitializeAsync();

        startupState.MarkDatabaseReady();
        if (markReady)
        {
            startupState.MarkVectorCacheReady();
            startupState.MarkOnnxReady();
        }

        app.MapOpenApi();
        app.MapScalarApiReference();
        app.MapMcp("/mcp");
        app.MapRestApi();

        await app.StartAsync();
        return app;
    }
}
