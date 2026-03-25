using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Data;
using HgvMate.Mcp.Search;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

[TestClass]
public sealed class IndexingServiceTests
{
    private string _tempDir = null!;
    private static int _counter;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task IndexRepoAsync_NoModel_SkipsIndexing()
    {
        var hgvOptions = new HgvMateOptions { DataPath = _tempDir };
        var syncOptions = new RepoSyncOptions { ClonePath = "repos" };
        var searchOptions = new SearchOptions();

        var repoPath = Path.Combine(_tempDir, "repos", "testrepo");
        Directory.CreateDirectory(repoPath);
        File.WriteAllText(Path.Combine(repoPath, "test.cs"), "public class Test {}");

        var reader = new SourceCodeReader(hgvOptions, syncOptions, NullLogger<SourceCodeReader>.Instance);
        var embedder = new OnnxEmbedder((Microsoft.ML.OnnxRuntime.InferenceSession?)null, NullLogger<OnnxEmbedder>.Instance);

        var id = System.Threading.Interlocked.Increment(ref _counter);
        using var conn = new SqliteConnection($"Data Source=idxtest{id};Mode=Memory;Cache=Shared");
        await conn.OpenAsync();
        var factory = new TestConnectionFactory(conn.ConnectionString);
        var vectorStore = new VectorStore(factory, NullLogger<VectorStore>.Instance);
        await vectorStore.EnsureSchemaAsync();

        var service = new IndexingService(vectorStore, embedder, reader, searchOptions, NullLogger<IndexingService>.Instance);
        await service.IndexRepoAsync("testrepo");

        var results = await vectorStore.SearchAsync(new float[384], "testrepo", 10);
        Assert.IsEmpty(results);
    }

    private sealed class TestConnectionFactory : ISqliteConnectionFactory
    {
        private readonly string _connStr;
        public TestConnectionFactory(string connStr) => _connStr = connStr;
        public SqliteConnection CreateConnection() => new SqliteConnection(_connStr);
    }

    // ─── IsIndexableFile regression tests ────────────────────────────────────

    [TestMethod]
    public void IsIndexableFile_AcceptsSourceCodeExtensions()
    {
        Assert.IsTrue(IndexingService.IsIndexableFile("/repo/src/Foo.cs"));
        Assert.IsTrue(IndexingService.IsIndexableFile("/repo/src/app.ts"));
        Assert.IsTrue(IndexingService.IsIndexableFile("/repo/README.md"));
        Assert.IsTrue(IndexingService.IsIndexableFile("/repo/config.json"));
    }

    [TestMethod]
    public void IsIndexableFile_RejectsNonSourceFiles()
    {
        Assert.IsFalse(IndexingService.IsIndexableFile("/repo/image.png"));
        Assert.IsFalse(IndexingService.IsIndexableFile("/repo/archive.zip"));
        Assert.IsFalse(IndexingService.IsIndexableFile("/repo/binary.dll"));
        Assert.IsFalse(IndexingService.IsIndexableFile("/repo/font.woff2"));
    }

    [TestMethod]
    public void IsIndexableFile_RejectsExcludedDirectories()
    {
        var sep = Path.DirectorySeparatorChar;
        Assert.IsFalse(IndexingService.IsIndexableFile($"/repo{sep}.git{sep}config"));
        Assert.IsFalse(IndexingService.IsIndexableFile($"/repo{sep}node_modules{sep}lodash{sep}index.js"));
        Assert.IsFalse(IndexingService.IsIndexableFile($"/repo{sep}bin{sep}Debug{sep}app.cs"));
        Assert.IsFalse(IndexingService.IsIndexableFile($"/repo{sep}obj{sep}project.assets.json"));
        Assert.IsFalse(IndexingService.IsIndexableFile($"/repo{sep}.gitnexus{sep}index.json"));
    }
}
