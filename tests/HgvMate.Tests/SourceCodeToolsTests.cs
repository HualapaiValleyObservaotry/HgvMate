using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Search;
using HgvMate.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

[TestClass]
public sealed class SourceCodeToolsTests
{
    private string _tempDir = null!;
    private SourceCodeTools _tools = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);

        var hgvOptions = new HgvMateOptions { DataPath = _tempDir };
        var syncOptions = new RepoSyncOptions { ClonePath = "repos" };
        var searchOptions = new SearchOptions();

        var reader = new SourceCodeReader(hgvOptions, syncOptions, NullLogger<SourceCodeReader>.Instance);

        // Create a VectorStore backed by a temp file
        var vectorStore = new VectorStore(Path.Combine(_tempDir, "vectors.bin"), NullLogger<VectorStore>.Instance);
        vectorStore.LoadAsync().GetAwaiter().GetResult();

        var embedder = new OnnxEmbedder((Microsoft.ML.OnnxRuntime.InferenceSession?)null,
            NullLogger<OnnxEmbedder>.Instance);
        var fakeRegistry = new FakeRepoRegistry();
        var grepService = new GitGrepSearchService(fakeRegistry, reader, searchOptions, NullLogger<GitGrepSearchService>.Instance);
        var hybridSearch = new HybridSearchService(grepService, vectorStore, embedder, searchOptions, NullLogger<HybridSearchService>.Instance);

        _tools = new SourceCodeTools(hybridSearch, reader);

        // Create a fake repo
        var repoPath = Path.Combine(_tempDir, "repos", "testrepo");
        Directory.CreateDirectory(repoPath);
        File.WriteAllText(Path.Combine(repoPath, "test.txt"), "Hello World");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task SearchSourceCode_EmptyQuery_ReturnsError()
    {
        var result = await _tools.SearchSourceCode("");
        StringAssert.Contains(result, "Error");
    }

    [TestMethod]
    public async Task GetFileContent_ValidFile_ReturnsContent()
    {
        var result = await _tools.GetFileContent("testrepo", "test.txt");
        Assert.AreEqual("Hello World", result);
    }

    [TestMethod]
    public async Task GetFileContent_PathTraversal_ReturnsError()
    {
        var result = await _tools.GetFileContent("testrepo", "../../etc/passwd");
        StringAssert.Contains(result, "Error");
    }

    [TestMethod]
    public async Task GetFileContent_EmptyRepository_ReturnsError()
    {
        var result = await _tools.GetFileContent("", "test.txt");
        StringAssert.Contains(result, "Error");
    }

    [TestMethod]
    public async Task GetFileContent_EmptyPath_ReturnsError()
    {
        var result = await _tools.GetFileContent("testrepo", "");
        StringAssert.Contains(result, "Error");
    }

    [TestMethod]
    public async Task GetFileContent_NonExistentFile_ReturnsError()
    {
        var result = await _tools.GetFileContent("testrepo", "nonexistent.txt");
        StringAssert.Contains(result, "Error");
    }
}
