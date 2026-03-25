using HgvMate.Mcp.Data;
using HgvMate.Mcp.Search;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

[TestClass]
public sealed class VectorStoreTests
{
    private SqliteConnection _connection = null!;
    private VectorStore _store = null!;
    private static int _counter;

    [TestInitialize]
    public async Task Setup()
    {
        var id = System.Threading.Interlocked.Increment(ref _counter);
        _connection = new SqliteConnection($"Data Source=vstoretest{id};Mode=Memory;Cache=Shared");
        await _connection.OpenAsync();
        var factory = new SharedConnectionFactory(_connection.ConnectionString);
        _store = new VectorStore(factory, NullLogger<VectorStore>.Instance);
        await _store.EnsureSchemaAsync();
    }

    [TestCleanup]
    public void Cleanup() => _connection.Dispose();

    [TestMethod]
    public async Task UpsertChunksAsync_ValidChunks_CanRetrieve()
    {
        await _store.UpsertChunksAsync([
            new SourceChunk("repo1", "src/Test.cs", 0, "public class Test {}", new float[384]),
        ]);
        var results = await _store.SearchAsync(new float[384], "repo1", 10);
        Assert.HasCount(1, results);
    }

    [TestMethod]
    public async Task UpsertChunksAsync_DuplicateChunk_Upserts()
    {
        var chunk1 = new SourceChunk("repo1", "src/Test.cs", 0, "original content", new float[384]);
        var chunk2 = new SourceChunk("repo1", "src/Test.cs", 0, "updated content", new float[384]);
        await _store.UpsertChunksAsync([chunk1]);
        await _store.UpsertChunksAsync([chunk2]);
        var results = await _store.SearchAsync(new float[384], "repo1", 10);
        Assert.HasCount(1, results);
        Assert.AreEqual("updated content", results[0].Content);
    }

    [TestMethod]
    public async Task DeleteChunksForFileAsync_RemovesOnlyTargetFile()
    {
        await _store.UpsertChunksAsync([
            new SourceChunk("repo1", "file1.cs", 0, "content1", new float[384]),
            new SourceChunk("repo1", "file2.cs", 0, "content2", new float[384]),
        ]);
        await _store.DeleteChunksForFileAsync("repo1", "file1.cs");
        var results = await _store.SearchAsync(new float[384], "repo1", 10);
        Assert.HasCount(1, results);
        Assert.AreEqual("file2.cs", results[0].FilePath);
    }

    [TestMethod]
    public async Task DeleteChunksForRepoAsync_RemovesAllRepoChunks()
    {
        await _store.UpsertChunksAsync([
            new SourceChunk("repo1", "file1.cs", 0, "content1", new float[384]),
            new SourceChunk("repo2", "file1.cs", 0, "content2", new float[384]),
        ]);
        await _store.DeleteChunksForRepoAsync("repo1");
        var results1 = await _store.SearchAsync(new float[384], "repo1", 10);
        var results2 = await _store.SearchAsync(new float[384], "repo2", 10);
        Assert.IsEmpty(results1);
        Assert.HasCount(1, results2);
    }

    [TestMethod]
    public async Task SearchAsync_ReturnsHighestSimilarityFirst()
    {
        var queryVec = new float[384];
        queryVec[0] = 1.0f;

        var chunk1Vec = new float[384];
        chunk1Vec[1] = 1.0f; // orthogonal to query

        var chunk2Vec = new float[384];
        chunk2Vec[0] = 1.0f; // same direction as query

        await _store.UpsertChunksAsync([
            new SourceChunk("repo1", "low.cs", 0, "low similarity", chunk1Vec),
            new SourceChunk("repo1", "high.cs", 0, "high similarity", chunk2Vec),
        ]);

        var results = await _store.SearchAsync(queryVec, "repo1", 10);
        Assert.HasCount(2, results);
        Assert.AreEqual("high.cs", results[0].FilePath);
    }

    [TestMethod]
    public async Task UpsertChunkAsync_SingleChunk_CanRetrieve()
    {
        await _store.UpsertChunkAsync(new SourceChunk("repo1", "single.cs", 0, "single content", new float[384]));
        var results = await _store.SearchAsync(new float[384], "repo1", 10);
        Assert.HasCount(1, results);
        Assert.AreEqual("single content", results[0].Content);
    }

    [TestMethod]
    public async Task SearchAsync_NoRepoFilter_ReturnsAllRepos()
    {
        await _store.UpsertChunksAsync([
            new SourceChunk("repoA", "a.cs", 0, "contentA", new float[384]),
            new SourceChunk("repoB", "b.cs", 0, "contentB", new float[384]),
        ]);
        var results = await _store.SearchAsync(new float[384], null, 10);
        Assert.HasCount(2, results);
    }

    private sealed class SharedConnectionFactory : ISqliteConnectionFactory
    {
        private readonly string _connStr;
        public SharedConnectionFactory(string connStr) => _connStr = connStr;
        public SqliteConnection CreateConnection() => new SqliteConnection(_connStr);
    }
}
