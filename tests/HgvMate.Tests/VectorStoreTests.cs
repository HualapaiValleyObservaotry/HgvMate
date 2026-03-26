using HgvMate.Mcp.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

[TestClass]
public sealed class VectorStoreTests
{
    private string _tempDir = null!;
    private VectorStore _store = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        var filePath = Path.Combine(_tempDir, "vectors.bin");
        _store = new VectorStore(filePath, NullLogger<VectorStore>.Instance);
        await _store.LoadAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void UpsertChunks_ValidChunks_CanRetrieve()
    {
        _store.UpsertChunks([
            new SourceChunk("repo1", "src/Test.cs", 0, "public class Test {}", new float[384]),
        ]);
        var results = _store.Search(new float[384], "repo1", 10);
        Assert.HasCount(1, results);
    }

    [TestMethod]
    public void UpsertChunks_DuplicateChunk_Upserts()
    {
        var chunk1 = new SourceChunk("repo1", "src/Test.cs", 0, "original content", new float[384]);
        var chunk2 = new SourceChunk("repo1", "src/Test.cs", 0, "updated content", new float[384]);
        _store.UpsertChunks([chunk1]);
        _store.UpsertChunks([chunk2]);
        var results = _store.Search(new float[384], "repo1", 10);
        Assert.HasCount(1, results);
        Assert.AreEqual("updated content", results[0].Content);
    }

    [TestMethod]
    public void DeleteChunksForFile_RemovesOnlyTargetFile()
    {
        _store.UpsertChunks([
            new SourceChunk("repo1", "file1.cs", 0, "content1", new float[384]),
            new SourceChunk("repo1", "file2.cs", 0, "content2", new float[384]),
        ]);
        _store.DeleteChunksForFile("repo1", "file1.cs");
        var results = _store.Search(new float[384], "repo1", 10);
        Assert.HasCount(1, results);
        Assert.AreEqual("file2.cs", results[0].FilePath);
    }

    [TestMethod]
    public void DeleteChunksForRepo_RemovesAllRepoChunks()
    {
        _store.UpsertChunks([
            new SourceChunk("repo1", "file1.cs", 0, "content1", new float[384]),
            new SourceChunk("repo2", "file1.cs", 0, "content2", new float[384]),
        ]);
        _store.DeleteChunksForRepo("repo1");
        var results1 = _store.Search(new float[384], "repo1", 10);
        var results2 = _store.Search(new float[384], "repo2", 10);
        Assert.IsEmpty(results1);
        Assert.HasCount(1, results2);
    }

    [TestMethod]
    public void Search_ReturnsHighestSimilarityFirst()
    {
        var queryVec = new float[384];
        queryVec[0] = 1.0f;

        var chunk1Vec = new float[384];
        chunk1Vec[1] = 1.0f; // orthogonal to query

        var chunk2Vec = new float[384];
        chunk2Vec[0] = 1.0f; // same direction as query

        _store.UpsertChunks([
            new SourceChunk("repo1", "low.cs", 0, "low similarity", chunk1Vec),
            new SourceChunk("repo1", "high.cs", 0, "high similarity", chunk2Vec),
        ]);

        var results = _store.Search(queryVec, "repo1", 10);
        Assert.HasCount(2, results);
        Assert.AreEqual("high.cs", results[0].FilePath);
    }

    [TestMethod]
    public void UpsertChunk_SingleChunk_CanRetrieve()
    {
        _store.UpsertChunk(new SourceChunk("repo1", "single.cs", 0, "single content", new float[384]));
        var results = _store.Search(new float[384], "repo1", 10);
        Assert.HasCount(1, results);
        Assert.AreEqual("single content", results[0].Content);
    }

    [TestMethod]
    public void Search_NoRepoFilter_ReturnsAllRepos()
    {
        _store.UpsertChunks([
            new SourceChunk("repoA", "a.cs", 0, "contentA", new float[384]),
            new SourceChunk("repoB", "b.cs", 0, "contentB", new float[384]),
        ]);
        var results = _store.Search(new float[384], null, 10);
        Assert.HasCount(2, results);
    }

    [TestMethod]
    public void Cache_IsLoadedAfterLoad()
    {
        Assert.IsTrue(_store.IsCacheLoaded);
    }

    [TestMethod]
    public void Cache_ReflectsUpsertsAndDeletes()
    {
        Assert.AreEqual(0, _store.CachedChunkCount);

        _store.UpsertChunks([
            new SourceChunk("repo1", "a.cs", 0, "content", new float[384]),
            new SourceChunk("repo1", "b.cs", 0, "content", new float[384]),
        ]);
        Assert.AreEqual(2, _store.CachedChunkCount);

        _store.DeleteChunksForFile("repo1", "a.cs");
        Assert.AreEqual(1, _store.CachedChunkCount);

        _store.DeleteChunksForRepo("repo1");
        Assert.AreEqual(0, _store.CachedChunkCount);
    }

    [TestMethod]
    public void GetChunkCounts_ReturnsByRepo()
    {
        _store.UpsertChunks([
            new SourceChunk("repoA", "a.cs", 0, "c1", new float[384]),
            new SourceChunk("repoA", "a.cs", 1, "c2", new float[384]),
            new SourceChunk("repoB", "b.cs", 0, "c3", new float[384]),
        ]);
        var counts = _store.GetChunkCounts();
        Assert.AreEqual(2, counts["repoA"]);
        Assert.AreEqual(1, counts["repoB"]);
    }

    [TestMethod]
    public async Task SaveAndLoad_RoundTripsData()
    {
        _store.UpsertChunks([
            new SourceChunk("repo1", "file.cs", 0, "hello world", new float[384]),
            new SourceChunk("repo2", "other.cs", 1, "other content", new float[384]),
        ]);
        await _store.SaveAsync();

        // Create a new store pointing at the same file
        var store2 = new VectorStore(
            Path.Combine(_tempDir, "vectors.bin"),
            NullLogger<VectorStore>.Instance);
        await store2.LoadAsync();

        Assert.AreEqual(2, store2.CachedChunkCount);
        var results = store2.Search(new float[384], "repo1", 10);
        Assert.HasCount(1, results);
        Assert.AreEqual("hello world", results[0].Content);
    }

    [TestMethod]
    public async Task SaveAndLoad_PreservesEmbeddings()
    {
        var embedding = new float[384];
        embedding[0] = 1.0f;
        embedding[100] = -0.5f;
        embedding[383] = 0.75f;

        _store.UpsertChunk(new SourceChunk("repo1", "f.cs", 0, "test", embedding));
        await _store.SaveAsync();

        var store2 = new VectorStore(
            Path.Combine(_tempDir, "vectors.bin"),
            NullLogger<VectorStore>.Instance);
        await store2.LoadAsync();

        var queryVec = new float[384];
        queryVec[0] = 1.0f;
        var results = store2.Search(queryVec, limit: 1);
        Assert.HasCount(1, results);
        Assert.IsGreaterThan(results[0].Score, 0.5f, "Embedding should be preserved and searchable.");
    }

    [TestMethod]
    public async Task Load_EmptyFile_StartsEmpty()
    {
        var filePath = Path.Combine(_tempDir, "nonexistent.bin");
        var store = new VectorStore(filePath, NullLogger<VectorStore>.Instance);
        await store.LoadAsync();
        Assert.IsTrue(store.IsCacheLoaded);
        Assert.AreEqual(0, store.CachedChunkCount);
    }
}
