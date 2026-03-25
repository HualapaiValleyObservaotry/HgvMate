using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Data;
using HgvMate.Mcp.Repos;
using HgvMate.Mcp.Search;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

[TestClass]
public sealed class RepoSyncServiceTests
{
    private string _tempDir = null!;
    private static int _counter;
    private readonly List<SqliteConnection> _connections = [];

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "HgvMateTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var conn in _connections)
            conn.Dispose();
        _connections.Clear();

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private (RepoSyncService service, TrackingIndexingService indexing, TrackingRegistry registry)
        BuildService(string tempDir, Dictionary<string[], (string output, int exit)>? gitResponses = null)
    {
        var hgvOptions = new HgvMateOptions { DataPath = tempDir };
        var syncOptions = new RepoSyncOptions { ClonePath = "repos" };
        var searchOptions = new SearchOptions();
        var credProvider = new FakeCredentialProvider();

        var registry = new TrackingRegistry();

        var id = System.Threading.Interlocked.Increment(ref _counter);
        var connStr = $"Data Source=rsstest{id};Mode=Memory;Cache=Shared";
        var conn = new SqliteConnection(connStr);
        conn.Open();
        _connections.Add(conn); // disposed in TestCleanup
        var factory = new TestConnFactory(connStr);
        var vectorStore = new VectorStore(factory, NullLogger<VectorStore>.Instance);
        vectorStore.EnsureSchemaAsync().GetAwaiter().GetResult();

        var embedder = new OnnxEmbedder((Microsoft.ML.OnnxRuntime.InferenceSession?)null, NullLogger<OnnxEmbedder>.Instance);
        var reader = new SourceCodeReader(hgvOptions, syncOptions, NullLogger<SourceCodeReader>.Instance);
        var trackingIndexing = new TrackingIndexingService(vectorStore, embedder, reader, searchOptions);

        var gitNexus = new GitNexusService(hgvOptions, syncOptions, NullLogger<GitNexusService>.Instance);

        var svc = new FakeGitRepoSyncService(
            registry, credProvider, hgvOptions, syncOptions,
            trackingIndexing, gitNexus,
            new HgvMate.Mcp.Configuration.StartupState(),
            NullLogger<RepoSyncService>.Instance,
            gitResponses ?? []);

        return (svc, trackingIndexing, registry);
    }

    // ─── GetChangedFilesAsync ────────────────────────────────────────────────

    [TestMethod]
    public async Task GetChangedFilesAsync_ReturnsFileList_WhenDiffSucceeds()
    {
        var responses = new Dictionary<string[], (string, int)>(StringArrayComparer.Instance)
        {
            { ["diff", "--name-only", "abc123..def456"], ("src/Foo.cs\nsrc/Bar.ts\n", 0) }
        };

        var (svc, _, _) = BuildService(_tempDir, responses);
        var clonePath = Path.Combine(_tempDir, "repos", "myrepo");
        Directory.CreateDirectory(clonePath);

        var files = await svc.GetChangedFilesAsync(clonePath, "abc123", "def456");

        Assert.HasCount(2, files);
        Assert.IsTrue(files.Contains("src/Foo.cs"));
        Assert.IsTrue(files.Contains("src/Bar.ts"));
    }

    [TestMethod]
    public async Task GetChangedFilesAsync_ReturnsEmpty_WhenGitFails()
    {
        var responses = new Dictionary<string[], (string, int)>(StringArrayComparer.Instance)
        {
            { ["diff", "--name-only", "abc123..def456"], ("", 128) }
        };

        var (svc, _, _) = BuildService(_tempDir, responses);
        var clonePath = Path.Combine(_tempDir, "repos", "myrepo");
        Directory.CreateDirectory(clonePath);

        var files = await svc.GetChangedFilesAsync(clonePath, "abc123", "def456");

        Assert.IsEmpty(files);
    }

    [TestMethod]
    public async Task GetChangedFilesAsync_ReturnsEmpty_WhenOutputIsEmpty()
    {
        var responses = new Dictionary<string[], (string, int)>(StringArrayComparer.Instance)
        {
            { ["diff", "--name-only", "abc123..abc123"], ("", 0) }
        };

        var (svc, _, _) = BuildService(_tempDir, responses);
        var clonePath = Path.Combine(_tempDir, "repos", "myrepo");
        Directory.CreateDirectory(clonePath);

        var files = await svc.GetChangedFilesAsync(clonePath, "abc123", "abc123");

        Assert.IsEmpty(files);
    }

    // ─── SyncRepoAsync – no-op ───────────────────────────────────────────────

    [TestMethod]
    public async Task SyncRepoAsync_NoReindex_WhenShaUnchanged()
    {
        const string sha = "aabbcc";
        var repo = new RepoRecord(1, "myrepo", "https://example.com/r.git", "main", "github", true, sha, null, null);

        var clonePath = Path.Combine(_tempDir, "repos", "myrepo");
        Directory.CreateDirectory(Path.Combine(clonePath, ".git")); // simulate existing clone

        // All git calls return the same SHA
        var responses = new Dictionary<string[], (string, int)>(StringArrayComparer.Instance)
        {
            { ["fetch", "--depth", "1", "origin", "main"], ("", 0) },
            { ["reset", "--hard", "origin/main"], ("", 0) },
            { ["rev-parse", "HEAD"], (sha + "\n", 0) }
        };

        var (svc, indexing, _) = BuildService(_tempDir, responses);

        await svc.SyncRepoAsync(repo);

        Assert.AreEqual(0, indexing.IndexRepoCalls, "Should not full-index when SHA is unchanged.");
        Assert.AreEqual(0, indexing.IndexFileCalls, "Should not incremental-index when SHA is unchanged.");
    }

    // ─── SyncRepoAsync – incremental re-index ────────────────────────────────

    [TestMethod]
    public async Task SyncRepoAsync_IndexesOnlyChangedFiles_WhenShaChanged()
    {
        const string oldSha = "aabbcc";
        const string newSha = "ddeeff";
        var repo = new RepoRecord(1, "myrepo", "https://example.com/r.git", "main", "github", true, oldSha, null, null);

        var clonePath = Path.Combine(_tempDir, "repos", "myrepo");
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));

        var responses = new Dictionary<string[], (string, int)>(StringArrayComparer.Instance)
        {
            { ["fetch", "--depth", "1", "origin", "main"], ("", 0) },
            { ["reset", "--hard", "origin/main"], ("", 0) },
            { ["rev-parse", "HEAD"], (newSha + "\n", 0) },
            { ["diff", "--name-only", $"{oldSha}..{newSha}"], ("src/Changed.cs\n", 0) }
        };

        var (svc, indexing, _) = BuildService(_tempDir, responses);

        await svc.SyncRepoAsync(repo);

        Assert.AreEqual(0, indexing.IndexRepoCalls, "Should NOT do a full re-index for changed SHA.");
        Assert.AreEqual(1, indexing.IndexFileCalls, "Should index only the changed file.");
        Assert.AreEqual("src/Changed.cs", indexing.IndexedFiles[0]);
    }

    [TestMethod]
    public async Task SyncRepoAsync_FallsBackToFullIndex_WhenDiffFails()
    {
        const string oldSha = "aabbcc";
        const string newSha = "ddeeff";
        var repo = new RepoRecord(1, "myrepo", "https://example.com/r.git", "main", "github", true, oldSha, null, null);

        var clonePath = Path.Combine(_tempDir, "repos", "myrepo");
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));

        var responses = new Dictionary<string[], (string, int)>(StringArrayComparer.Instance)
        {
            { ["fetch", "--depth", "1", "origin", "main"], ("", 0) },
            { ["reset", "--hard", "origin/main"], ("", 0) },
            { ["rev-parse", "HEAD"], (newSha + "\n", 0) },
            { ["diff", "--name-only", $"{oldSha}..{newSha}"], ("", 128) } // git diff fails
        };

        var (svc, indexing, _) = BuildService(_tempDir, responses);

        await svc.SyncRepoAsync(repo);

        Assert.AreEqual(1, indexing.IndexRepoCalls, "Should fall back to full re-index when diff fails.");
        Assert.AreEqual(0, indexing.IndexFileCalls);
    }

    // ─── SyncRepoAsync – first sync ──────────────────────────────────────────

    [TestMethod]
    public async Task SyncRepoAsync_FullIndexOnFirstSync_WhenNoLastSha()
    {
        var repo = new RepoRecord(1, "newrepo", "https://example.com/r.git", "main", "github", true, null, null, null);

        var clonePath = Path.Combine(_tempDir, "repos", "newrepo");
        // .git does NOT exist → clone path
        Directory.CreateDirectory(clonePath);

        const string sha = "aabbcc";
        var responses = new Dictionary<string[], (string, int)>(StringArrayComparer.Instance)
        {
            {
                ["clone", "--depth", "1", "--single-branch", "--branch", "main", "https://example.com/r.git", "."],
                ("", 0)
            },
            { ["rev-parse", "HEAD"], (sha + "\n", 0) }
        };

        var (svc, indexing, registry) = BuildService(_tempDir, responses);

        await svc.SyncRepoAsync(repo);

        Assert.AreEqual(1, indexing.IndexRepoCalls, "First sync should trigger a full index.");
        Assert.AreEqual(0, indexing.IndexFileCalls);
        Assert.IsTrue(registry.LastShaUpdates.ContainsKey("newrepo"));
        Assert.AreEqual(sha, registry.LastShaUpdates["newrepo"]);
    }

    // ─── Error handling ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task SyncRepoAsync_DoesNotThrow_WhenGitCommandsFail()
    {
        var repo = new RepoRecord(1, "badrepo", "https://example.com/r.git", "main", "github", true, null, null, null);
        var clonePath = Path.Combine(_tempDir, "repos", "badrepo");
        Directory.CreateDirectory(clonePath);

        // All git commands fail
        var responses = new Dictionary<string[], (string, int)>(StringArrayComparer.Instance)
        {
            {
                ["clone", "--depth", "1", "--single-branch", "--branch", "main", "https://example.com/r.git", "."],
                ("", 1)
            },
            { ["rev-parse", "HEAD"], ("", 128) }
        };

        var (svc, _, _) = BuildService(_tempDir, responses);

        // Should not throw — errors are caught and logged
        await svc.SyncRepoAsync(repo);
    }

    // ─── Regression: UpdateLastSha ordering ──────────────────────────────────

    [TestMethod]
    public async Task SyncRepoAsync_UpdatesShaAfterIndexing_NotBefore()
    {
        const string oldSha = "aabbcc";
        const string newSha = "ddeeff";
        var repo = new RepoRecord(1, "myrepo", "https://example.com/r.git", "main", "github", true, oldSha, null, null);

        var clonePath = Path.Combine(_tempDir, "repos", "myrepo");
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));

        var responses = new Dictionary<string[], (string, int)>(StringArrayComparer.Instance)
        {
            { ["fetch", "--depth", "1", "origin", "main"], ("", 0) },
            { ["reset", "--hard", "origin/main"], ("", 0) },
            { ["rev-parse", "HEAD"], (newSha + "\n", 0) },
            { ["diff", "--name-only", $"{oldSha}..{newSha}"], ("src/Changed.cs\n", 0) }
        };

        var (svc, indexing, registry) = BuildService(_tempDir, responses);

        await svc.SyncRepoAsync(repo);

        // SHA should be updated only after indexing completes
        Assert.IsTrue(registry.LastShaUpdates.ContainsKey("myrepo"), "SHA should be updated after sync.");
        Assert.AreEqual(newSha, registry.LastShaUpdates["myrepo"]);
        Assert.IsTrue(indexing.IndexFileCalls > 0 || indexing.IndexRepoCalls > 0,
            "Indexing should have been called before SHA update.");
    }

    // ─── Regression: empty SHA handling ──────────────────────────────────────

    [TestMethod]
    public async Task SyncRepoAsync_FallsBackToFullIndex_WhenShaIsEmpty()
    {
        var repo = new RepoRecord(1, "emptysha", "https://example.com/r.git", "main", "github", true, "oldsha", null, null);

        var clonePath = Path.Combine(_tempDir, "repos", "emptysha");
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));

        // rev-parse returns empty (simulating failure)
        var responses = new Dictionary<string[], (string, int)>(StringArrayComparer.Instance)
        {
            { ["fetch", "--depth", "1", "origin", "main"], ("", 0) },
            { ["reset", "--hard", "origin/main"], ("", 0) },
            { ["rev-parse", "HEAD"], ("\n", 0) }
        };

        var (svc, indexing, registry) = BuildService(_tempDir, responses);

        await svc.SyncRepoAsync(repo);

        // Should trigger full re-index as fallback when SHA is empty
        Assert.AreEqual(1, indexing.IndexRepoCalls, "Should fall back to full re-index when SHA is empty.");
        Assert.IsFalse(registry.LastShaUpdates.ContainsKey("emptysha"),
            "Should NOT update SHA when it could not be determined.");
    }

    // ─── Disk space check ──────────────────────────────────────────────────

    [TestMethod]
    public void EnsureSufficientDiskSpace_DoesNotThrow_WhenDisabled()
    {
        var hgvOptions = new HgvMateOptions { DataPath = _tempDir };
        var syncOptions = new RepoSyncOptions { ClonePath = "repos", MinFreeDiskSpaceMb = 0 };
        var (svc, _, _) = BuildService(_tempDir);

        // Should not throw — check is disabled
        svc.EnsureSufficientDiskSpace(_tempDir);
    }

    [TestMethod]
    public void EnsureSufficientDiskSpace_Throws_WhenInsufficientSpace()
    {
        var (svc, _, _) = BuildServiceWithMinDiskSpace(_tempDir, long.MaxValue / (1024 * 1024));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            svc.EnsureSufficientDiskSpace(_tempDir));
    }

    [TestMethod]
    public void EnsureSufficientDiskSpace_DoesNotThrow_WhenSufficientSpace()
    {
        var (svc, _, _) = BuildServiceWithMinDiskSpace(_tempDir, 1);

        // Should not throw — 1 MB is likely available
        svc.EnsureSufficientDiskSpace(_tempDir);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IsTransientError_ReturnsFalse_ForOperationCanceledException()
    {
        Assert.IsFalse(RepoSyncService.IsTransientError(new OperationCanceledException()));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IsTransientError_ReturnsFalse_ForTaskCanceledException()
    {
        Assert.IsFalse(RepoSyncService.IsTransientError(new TaskCanceledException()));
    }

    private (RepoSyncService service, TrackingIndexingService indexing, TrackingRegistry registry)
        BuildServiceWithMinDiskSpace(string tempDir, long minMb)
    {
        var hgvOptions = new HgvMateOptions { DataPath = tempDir };
        var syncOptions = new RepoSyncOptions { ClonePath = "repos", MinFreeDiskSpaceMb = minMb };
        var credProvider = new FakeCredentialProvider();
        var registry = new TrackingRegistry();

        var id = System.Threading.Interlocked.Increment(ref _counter);
        var connStr = $"Data Source=rsstest{id};Mode=Memory;Cache=Shared";
        var conn = new SqliteConnection(connStr);
        conn.Open();
        _connections.Add(conn);
        var factory = new TestConnFactory(connStr);
        var vectorStore = new VectorStore(factory, NullLogger<VectorStore>.Instance);
        vectorStore.EnsureSchemaAsync().GetAwaiter().GetResult();

        var embedder = new OnnxEmbedder((Microsoft.ML.OnnxRuntime.InferenceSession?)null, NullLogger<OnnxEmbedder>.Instance);
        var reader = new SourceCodeReader(hgvOptions, syncOptions, NullLogger<SourceCodeReader>.Instance);
        var trackingIndexing = new TrackingIndexingService(vectorStore, embedder, reader, new SearchOptions());
        var gitNexus = new GitNexusService(hgvOptions, syncOptions, NullLogger<GitNexusService>.Instance);

        var svc = new FakeGitRepoSyncService(
            registry, credProvider, hgvOptions, syncOptions,
            trackingIndexing, gitNexus,
            new HgvMate.Mcp.Configuration.StartupState(),
            NullLogger<RepoSyncService>.Instance, []);

        return (svc, trackingIndexing, registry);
    }

    // ─── Inner test helpers ──────────────────────────────────────────────────

    /// <summary>Subclass that intercepts RunGitAsync and returns canned responses.</summary>
    private sealed class FakeGitRepoSyncService : RepoSyncService
    {
        private readonly Dictionary<string[], (string output, int exit)> _responses;

        public FakeGitRepoSyncService(
            IRepoRegistry registry,
            IGitCredentialProvider credentialProvider,
            HgvMateOptions hgvMateOptions,
            RepoSyncOptions syncOptions,
            IndexingService indexingService,
            GitNexusService gitNexusService,
            HgvMate.Mcp.Configuration.StartupState startupState,
            Microsoft.Extensions.Logging.ILogger<RepoSyncService> logger,
            Dictionary<string[], (string output, int exit)> responses)
            : base(registry, credentialProvider, hgvMateOptions, syncOptions, indexingService, gitNexusService, startupState, logger)
        {
            _responses = responses;
        }

        protected override Task<(string output, int exitCode)> RunGitAsync(
            string[] args, string workingDirectory, CancellationToken cancellationToken)
        {
            foreach (var (key, value) in _responses)
            {
                if (key.Length == args.Length && key.Zip(args).All(p => p.First == p.Second))
                    return Task.FromResult((value.output, value.exit));
            }
            // No match — return a successful empty response
            return Task.FromResult(("", 0));
        }
    }

    /// <summary>Wraps a real IndexingService, tracking how many times each method is called.</summary>
    private sealed class TrackingIndexingService : IndexingService
    {
        public int IndexRepoCalls { get; private set; }
        public int IndexFileCalls { get; private set; }
        public List<string> IndexedFiles { get; } = [];

        public TrackingIndexingService(
            VectorStore vectorStore,
            IOnnxEmbedder embedder,
            SourceCodeReader reader,
            SearchOptions searchOptions)
            : base(vectorStore, embedder, reader, searchOptions, NullLogger<IndexingService>.Instance)
        { }

        public override Task<IndexResult> IndexRepoAsync(string repoName, CancellationToken cancellationToken = default)
        {
            IndexRepoCalls++;
            return base.IndexRepoAsync(repoName, cancellationToken);
        }

        public override Task IndexFileAsync(string repoName, string relativePath, CancellationToken cancellationToken = default)
        {
            IndexFileCalls++;
            IndexedFiles.Add(relativePath);
            return base.IndexFileAsync(repoName, relativePath, cancellationToken);
        }
    }

    /// <summary>Tracks UpdateLastShaAsync calls.</summary>
    private sealed class TrackingRegistry : FakeRepoRegistry
    {
        public Dictionary<string, string> LastShaUpdates { get; } = [];

        public override Task<bool> UpdateLastShaAsync(string name, string sha)
        {
            LastShaUpdates[name] = sha;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeCredentialProvider : IGitCredentialProvider
    {
        public string? GetToken(string source) => null;
        public string BuildAuthenticatedUrl(string url, string source) => url;
    }

    private sealed class TestConnFactory : ISqliteConnectionFactory
    {
        private readonly string _connStr;
        public TestConnFactory(string connStr) => _connStr = connStr;
        public SqliteConnection CreateConnection() => new SqliteConnection(_connStr);
    }

    /// <summary>Compares string[] keys for dictionary lookups by content.</summary>
    private sealed class StringArrayComparer : IEqualityComparer<string[]>
    {
        public static readonly StringArrayComparer Instance = new();
        public bool Equals(string[]? x, string[]? y)
            => x != null && y != null && x.Length == y.Length && x.Zip(y).All(p => p.First == p.Second);
        public int GetHashCode(string[] obj)
            => obj.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode()));
    }
}
