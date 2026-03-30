using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

[TestClass]
public sealed class SourceCodeReaderTests
{
    private string _tempDir = null!;
    private SourceCodeReader _reader = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);

        var hgvOptions = new HgvMateOptions { DataPath = _tempDir };
        var syncOptions = new RepoSyncOptions { ClonePath = "repos" };
        _reader = new SourceCodeReader(hgvOptions, syncOptions, NullLogger<SourceCodeReader>.Instance);

        // Create a fake repo with some files
        var repoPath = Path.Combine(_tempDir, "repos", "testrepo");
        Directory.CreateDirectory(repoPath);
        Directory.CreateDirectory(Path.Combine(repoPath, "src"));
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# Test Repo");
        File.WriteAllText(Path.Combine(repoPath, "src", "Program.cs"), "Console.WriteLine(\"Hello\");");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task GetFileAsync_ValidFile_ReturnsContent()
    {
        var content = await _reader.GetFileAsync("testrepo", "README.md");
        Assert.AreEqual("# Test Repo", content);
    }

    [TestMethod]
    public async Task GetFileAsync_NestedFile_ReturnsContent()
    {
        var content = await _reader.GetFileAsync("testrepo", "src/Program.cs");
        Assert.AreEqual("Console.WriteLine(\"Hello\");", content);
    }

    [TestMethod]
    public async Task GetFileAsync_PathTraversal_ThrowsUnauthorized()
    {
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
            () => _reader.GetFileAsync("testrepo", "../../../etc/passwd"));
    }

    [TestMethod]
    public async Task GetFileAsync_PathTraversalWithEncoding_ThrowsUnauthorized()
    {
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
            () => _reader.GetFileAsync("testrepo", "../../sensitive"));
    }

    [TestMethod]
    public async Task GetFileAsync_NonExistentFile_ThrowsFileNotFound()
    {
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => _reader.GetFileAsync("testrepo", "nonexistent.txt"));
    }

    [TestMethod]
    public async Task GetFileAsync_EmptyPath_ThrowsArgumentException()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => _reader.GetFileAsync("testrepo", ""));
    }

    [TestMethod]
    public async Task GetFileAsync_EmptyRepoName_ThrowsArgumentException()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => _reader.GetFileAsync("", "README.md"));
    }

    [TestMethod]
    public async Task ListDirectoryAsync_RootDirectory_ReturnsEntries()
    {
        var entries = await _reader.ListDirectoryAsync("testrepo", "");
        Assert.IsGreaterThanOrEqualTo(2, entries.Count);
        Assert.IsTrue(entries.Any(e => e.EndsWith("README.md") || e == "README.md"));
    }

    [TestMethod]
    public async Task ListDirectoryAsync_PathTraversal_ThrowsUnauthorized()
    {
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
            () => _reader.ListDirectoryAsync("testrepo", "../../"));
    }
}
