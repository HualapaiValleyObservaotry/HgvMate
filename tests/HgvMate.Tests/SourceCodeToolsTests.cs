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
    public async Task Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);

        var hgvOptions = new HgvMateOptions { DataPath = _tempDir };
        var syncOptions = new RepoSyncOptions { ClonePath = "repos" };
        var searchOptions = new SearchOptions();

        var reader = new SourceCodeReader(hgvOptions, syncOptions, NullLogger<SourceCodeReader>.Instance);

        // Create a VectorStore backed by a temp file
        var vectorStore = new VectorStore(Path.Combine(_tempDir, "vectors.bin"), NullLogger<VectorStore>.Instance);
        await vectorStore.LoadAsync();

        var embedder = new OnnxEmbedder((Microsoft.ML.OnnxRuntime.InferenceSession?)null,
            NullLogger<OnnxEmbedder>.Instance);
        var fakeRegistry = new FakeRepoRegistry();
        var grepService = new GitGrepSearchService(fakeRegistry, reader, searchOptions, NullLogger<GitGrepSearchService>.Instance);
        var hybridSearch = new HybridSearchService(grepService, vectorStore, embedder, searchOptions, NullLogger<HybridSearchService>.Instance);

        _tools = new SourceCodeTools(hybridSearch, reader);

        // Create a fake repo with multiple files
        var repoPath = Path.Combine(_tempDir, "repos", "testrepo");
        Directory.CreateDirectory(Path.Combine(repoPath, "src"));
        File.WriteAllText(Path.Combine(repoPath, "test.txt"), "Hello World");
        File.WriteAllText(Path.Combine(repoPath, "src", "main.cs"), "public class Main {}");
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
    public async Task SearchSourceCode_WhitespaceQuery_ReturnsError()
    {
        var result = await _tools.SearchSourceCode("   ");
        StringAssert.Contains(result, "Error");
    }

    [TestMethod]
    public async Task SearchSourceCode_ValidQuery_NoGitRepo_ReturnsNoResults()
    {
        // No git repos initialized so git grep won't work, but shouldn't crash
        var result = await _tools.SearchSourceCode("Hello");
        // Should return a clean response (either results or "no results")
        Assert.IsFalse(string.IsNullOrEmpty(result));
    }

    [TestMethod]
    public async Task GetFileContent_ValidFile_ReturnsContent()
    {
        var result = await _tools.GetFileContent("testrepo", "test.txt");
        Assert.AreEqual("Hello World", result);
    }

    [TestMethod]
    public async Task GetFileContent_NestedFile_ReturnsContent()
    {
        var result = await _tools.GetFileContent("testrepo", "src/main.cs");
        Assert.AreEqual("public class Main {}", result);
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

    [TestMethod]
    public async Task GetFileContent_NonExistentRepo_ReturnsError()
    {
        var result = await _tools.GetFileContent("no-such-repo", "test.txt");
        StringAssert.Contains(result, "Error");
    }

    // ── GetRepoTree ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetRepoTree_EmptyRepositoryName_ReturnsError()
    {
        var result = await _tools.GetRepoTree("");
        StringAssert.Contains(result, "Error");
    }

    [TestMethod]
    public async Task GetRepoTree_DepthZero_ReturnsError()
    {
        var result = await _tools.GetRepoTree("testrepo", depth: 0);
        StringAssert.Contains(result, "Error");
    }

    [TestMethod]
    public async Task GetRepoTree_DepthTooLarge_ReturnsError()
    {
        var result = await _tools.GetRepoTree("testrepo", depth: 11);
        StringAssert.Contains(result, "Error");
    }

    [TestMethod]
    public async Task GetRepoTree_NotClonedRepo_ReturnsError()
    {
        var result = await _tools.GetRepoTree("no-such-repo");
        StringAssert.Contains(result, "Error");
    }

    // ── FindFiles ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task FindFiles_EmptyRepositoryName_ReturnsError()
    {
        var result = await _tools.FindFiles("", "*.txt");
        StringAssert.Contains(result, "Error");
    }

    [TestMethod]
    public async Task FindFiles_EmptyPattern_ReturnsError()
    {
        var result = await _tools.FindFiles("testrepo", "");
        StringAssert.Contains(result, "Error");
    }

    [TestMethod]
    public async Task FindFiles_NotClonedRepo_ReturnsError()
    {
        var result = await _tools.FindFiles("no-such-repo", "*.txt");
        StringAssert.Contains(result, "Error");
    }

    // ── GetTechStack ────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetTechStack_EmptyRepositoryName_ReturnsError()
    {
        var result = await _tools.GetTechStack("");
        StringAssert.Contains(result, "Error");
    }

    [TestMethod]
    public async Task GetTechStack_NoTechStackFile_ReturnsNotFoundMessage()
    {
        var result = await _tools.GetTechStack("testrepo");
        // No .hgvmate/techstack.yml exists, should return a helpful message
        StringAssert.Contains(result, "techstack.yml");
    }

    [TestMethod]
    public async Task GetTechStack_ExistingTechStackFile_ReturnsContent()
    {
        // Create the .hgvmate/techstack.yml file in the test repo
        var hgvDir = Path.Combine(_tempDir, "repos", "testrepo", ".hgvmate");
        Directory.CreateDirectory(hgvDir);
        const string techstackContent = "name: testrepo\nlanguage: C#\n";
        File.WriteAllText(Path.Combine(hgvDir, "techstack.yml"), techstackContent);

        var result = await _tools.GetTechStack("testrepo");
        Assert.AreEqual(techstackContent, result);
    }

    // ── SearchSourceCode with filters ───────────────────────────────────

    [TestMethod]
    public async Task SearchSourceCode_WithIncludeExtensions_ValidQuery_ReturnsResult()
    {
        var result = await _tools.SearchSourceCode("hello", includeExtensions: ".cs,.ts");
        // No git repos indexed so just verify it doesn't crash and returns a clean response
        Assert.IsFalse(string.IsNullOrEmpty(result));
    }

    [TestMethod]
    public async Task SearchSourceCode_WithExcludePatterns_ValidQuery_ReturnsResult()
    {
        var result = await _tools.SearchSourceCode("hello", excludePatterns: "*.min.js,package-lock.json");
        Assert.IsFalse(string.IsNullOrEmpty(result));
    }
}
