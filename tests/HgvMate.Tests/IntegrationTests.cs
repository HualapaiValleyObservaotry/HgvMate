using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Data;
using HgvMate.Mcp.Repos;
using HgvMate.Mcp.Search;
using HgvMate.Mcp.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

/// <summary>
/// End-to-end integration test: creates a local git repo, registers it, syncs it,
/// and verifies that git-grep search works.  No internet or ONNX model required.
/// </summary>
[TestClass]
public sealed class IntegrationTests
{
    private string _tempDir = null!;
    private SqliteConnection _conn = null!;
    private static int _counter;

    [TestInitialize]
    public async Task Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "HgvMateInteg_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);

        var id = System.Threading.Interlocked.Increment(ref _counter);
        _conn = new SqliteConnection($"Data Source=integtest{id};Mode=Memory;Cache=Shared");
        await _conn.OpenAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _conn.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task EndToEnd_AddRepo_Sync_SearchFindsContent()
    {
        // ── 0. Verify git is available ─────────────────────────────────────
        if (!IsGitAvailable())
            Assert.Inconclusive("git is not available in this environment.");

        // ── 1. Create a bare local git repo with a source file ─────────────
        var originDir = Path.Combine(_tempDir, "origin.git");
        var workDir = Path.Combine(_tempDir, "work");
        Directory.CreateDirectory(workDir);

        RunGit(workDir, "init -b main");
        RunGit(workDir, "config user.email test@test.com");
        RunGit(workDir, "config user.name Test");
        File.WriteAllText(Path.Combine(workDir, "Greeter.cs"),
            "public class Greeter { public string Hello() => \"Hello, world!\"; }");
        RunGit(workDir, "add .");
        RunGit(workDir, "commit -m \"Initial commit\"");

        // Create a bare clone so we can clone from it
        RunGit(_tempDir, $"clone --bare {workDir} origin.git");

        var repoUrl = new Uri(originDir).AbsoluteUri;

        // ── 2. Wire up services ────────────────────────────────────────────
        var hgvOptions = new HgvMateOptions { DataPath = _tempDir };
        var syncOptions = new RepoSyncOptions { ClonePath = "repos", PollIntervalMinutes = 0 };
        var searchOptions = new SearchOptions { MaxResults = 10 };
        var credOptions = new CredentialOptions();

        var factory = new SharedConnFactory(_conn.ConnectionString);
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        var registry = new SqliteRepoRegistry(factory, NullLogger<SqliteRepoRegistry>.Instance);
        var credProvider = new GitCredentialProvider(credOptions, NullLogger<GitCredentialProvider>.Instance);

        var embedder = new OnnxEmbedder((Microsoft.ML.OnnxRuntime.InferenceSession?)null, NullLogger<OnnxEmbedder>.Instance);
        var reader = new SourceCodeReader(hgvOptions, syncOptions, NullLogger<SourceCodeReader>.Instance);
        var vectorStore = new VectorStore(factory, NullLogger<VectorStore>.Instance);
        await vectorStore.EnsureSchemaAsync();

        var indexingService = new IndexingService(vectorStore, embedder, reader, searchOptions, NullLogger<IndexingService>.Instance);
        var gitNexus = new GitNexusService(hgvOptions, syncOptions, NullLogger<GitNexusService>.Instance);
        var syncService = new RepoSyncService(registry, credProvider, hgvOptions, syncOptions, indexingService, gitNexus, NullLogger<RepoSyncService>.Instance);

        var gitGrep = new GitGrepSearchService(registry, reader, searchOptions, NullLogger<GitGrepSearchService>.Instance);
        var hybridSearch = new HybridSearchService(gitGrep, vectorStore, embedder, searchOptions, NullLogger<HybridSearchService>.Instance);
        var sourceTools = new SourceCodeTools(hybridSearch, reader);

        // ── 3. Register the repo ───────────────────────────────────────────
        await registry.AddAsync("greeter", repoUrl, "main", "local");

        // ── 4. Sync (clone + index) ────────────────────────────────────────
        await syncService.SyncAllAsync();

        // ── 5. Verify repo was cloned ──────────────────────────────────────
        var clonePath = syncService.GetClonePath("greeter");
        Assert.IsTrue(Directory.Exists(clonePath), "Clone directory should exist after sync.");
        Assert.IsTrue(File.Exists(Path.Combine(clonePath, "Greeter.cs")), "Greeter.cs should be cloned.");

        // ── 6. Verify SHA was recorded ─────────────────────────────────────
        var record = await registry.GetByNameAsync("greeter");
        Assert.IsNotNull(record);
        Assert.IsFalse(string.IsNullOrEmpty(record.LastSha), "LastSha should be set after sync.");

        // ── 7. Search for content via git grep ─────────────────────────────
        var searchResult = await sourceTools.SearchSourceCode("Hello", "greeter");
        Assert.IsTrue(searchResult.Contains("Greeter.cs") || searchResult.Contains("Hello"),
            $"Search should find 'Hello' in Greeter.cs. Got: {searchResult}");
    }

    [TestMethod]
    public async Task EndToEnd_IncrementalReindex_WhenFileChanges()
    {
        if (!IsGitAvailable())
            Assert.Inconclusive("git is not available in this environment.");

        // ── 1. Create initial repo ─────────────────────────────────────────
        var workDir = Path.Combine(_tempDir, "work2");
        Directory.CreateDirectory(workDir);
        RunGit(workDir, "init -b main");
        RunGit(workDir, "config user.email test@test.com");
        RunGit(workDir, "config user.name Test");
        File.WriteAllText(Path.Combine(workDir, "App.cs"), "class App { }");
        RunGit(workDir, "add .");
        RunGit(workDir, "commit -m \"v1\"");
        RunGit(_tempDir, $"clone --bare {workDir} origin2.git");

        var originDir = Path.Combine(_tempDir, "origin2.git");
        var repoUrl = new Uri(originDir).AbsoluteUri;

        // ── 2. Wire up services ────────────────────────────────────────────
        var hgvOptions = new HgvMateOptions { DataPath = _tempDir };
        var syncOptions = new RepoSyncOptions { ClonePath = "repos2", PollIntervalMinutes = 0 };
        var searchOptions = new SearchOptions { MaxResults = 10 };
        var credOptions = new CredentialOptions();

        var factory = new SharedConnFactory(_conn.ConnectionString);
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        var registry = new SqliteRepoRegistry(factory, NullLogger<SqliteRepoRegistry>.Instance);
        var credProvider = new GitCredentialProvider(credOptions, NullLogger<GitCredentialProvider>.Instance);
        var embedder = new OnnxEmbedder((Microsoft.ML.OnnxRuntime.InferenceSession?)null, NullLogger<OnnxEmbedder>.Instance);
        var reader = new SourceCodeReader(hgvOptions, syncOptions, NullLogger<SourceCodeReader>.Instance);
        var vectorStore = new VectorStore(factory, NullLogger<VectorStore>.Instance);
        await vectorStore.EnsureSchemaAsync();
        var indexingService = new TrackingIndexingService(vectorStore, embedder, reader, searchOptions);
        var gitNexus = new GitNexusService(hgvOptions, syncOptions, NullLogger<GitNexusService>.Instance);
        var syncService = new RepoSyncService(registry, credProvider, hgvOptions, syncOptions, indexingService, gitNexus, NullLogger<RepoSyncService>.Instance);

        // ── 3. First sync (full index) ─────────────────────────────────────
        await registry.AddAsync("app", repoUrl, "main", "local");
        await syncService.SyncAllAsync();

        var firstRepoIndexCalls = indexingService.IndexRepoCalls;
        Assert.AreEqual(1, firstRepoIndexCalls, "First sync should trigger a full index.");

        // ── 4. Add a commit to the origin ─────────────────────────────────
        File.WriteAllText(Path.Combine(workDir, "App.cs"), "class App { void Run() {} }");
        RunGit(workDir, "add .");
        RunGit(workDir, "commit -m \"v2\"");

        // Push the new commit to bare origin
        RunGit(workDir, $"push {originDir} main");

        // ── 5. Second sync: should detect changes and do incremental index ──
        var repoRecord = await registry.GetByNameAsync("app");
        Assert.IsNotNull(repoRecord);
        await syncService.SyncRepoAsync(repoRecord);

        // With shallow clones, diff may not work → falls back to full re-index.
        // Either way, re-indexing must have been triggered.
        var totalIndexCalls = indexingService.IndexRepoCalls + indexingService.IndexFileCalls;
        Assert.IsGreaterThan(firstRepoIndexCalls, totalIndexCalls,
            "Second sync should trigger re-indexing when content changed.");
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private static bool IsGitAvailable()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void RunGit(string workingDir, string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start git");

        var exited = p.WaitForExit(30_000);
        if (!exited)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                $"git command timed out after 30s. WorkingDirectory='{workingDir}', Arguments='{args}'");
        }

        if (p.ExitCode != 0)
        {
            var stderr = p.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"git {args} failed with exit code {p.ExitCode}. WorkingDirectory='{workingDir}'. Stderr: {stderr}");
        }
    }

    private sealed class SharedConnFactory : ISqliteConnectionFactory
    {
        private readonly string _connStr;
        public SharedConnFactory(string connStr) => _connStr = connStr;
        public SqliteConnection CreateConnection() => new SqliteConnection(_connStr);
    }

    private sealed class TrackingIndexingService : IndexingService
    {
        public int IndexRepoCalls { get; private set; }
        public int IndexFileCalls { get; private set; }

        public TrackingIndexingService(
            VectorStore vectorStore,
            IOnnxEmbedder embedder,
            SourceCodeReader reader,
            SearchOptions searchOptions)
            : base(vectorStore, embedder, reader, searchOptions, NullLogger<IndexingService>.Instance)
        { }

        public override Task IndexRepoAsync(string repoName, CancellationToken cancellationToken = default)
        {
            IndexRepoCalls++;
            return base.IndexRepoAsync(repoName, cancellationToken);
        }

        public override Task IndexFileAsync(string repoName, string relativePath, CancellationToken cancellationToken = default)
        {
            IndexFileCalls++;
            return base.IndexFileAsync(repoName, relativePath, cancellationToken);
        }
    }
}
