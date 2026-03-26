using HgvMate.Mcp.Configuration;
using Microsoft.Extensions.Configuration;

namespace HgvMate.Tests;

[TestClass]
public sealed class ConfigurationTests
{
    [TestMethod]
    public void HgvMateOptions_DefaultValues_AreCorrect()
    {
        var options = new HgvMateOptions();
        Assert.AreEqual("./data", options.DataPath);
        Assert.AreEqual("stdio", options.Transport);
    }

    [TestMethod]
    public void RepoSyncOptions_DefaultValues_AreCorrect()
    {
        var options = new RepoSyncOptions();
        Assert.AreEqual(15, options.PollIntervalMinutes);
        Assert.AreEqual("repos", options.ClonePath);
    }

    [TestMethod]
    public void SearchOptions_DefaultValues_AreCorrect()
    {
        var options = new SearchOptions();
        Assert.AreEqual(20, options.MaxResults);
        Assert.AreEqual(800, options.ChunkSize);
        Assert.AreEqual(100, options.ChunkOverlap);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ResolveCloneRoot_RelativePath_CombinesWithDataPath()
    {
        var options = new RepoSyncOptions { ClonePath = "repos" };
        var result = options.ResolveCloneRoot("/data");
        Assert.AreEqual(Path.Combine("/data", "repos"), result);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ResolveCloneRoot_AbsolutePath_ReturnsUnchanged()
    {
        var options = new RepoSyncOptions { ClonePath = "/tmp/hgvmate/repos" };
        var result = options.ResolveCloneRoot("/data");
        Assert.AreEqual("/tmp/hgvmate/repos", result);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ResolveCloneRoot_NestedRelativePath_CombinesWithDataPath()
    {
        var options = new RepoSyncOptions { ClonePath = "local/repos" };
        var result = options.ResolveCloneRoot("/var/hgvmate");
        Assert.AreEqual(Path.Combine("/var/hgvmate", "local/repos"), result);
    }

    [TestMethod]
    public void HgvMateOptions_CanBindFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HgvMate:DataPath"] = "/custom/data",
                ["HgvMate:Transport"] = "sse"
            })
            .Build();

        var options = new HgvMateOptions();
        config.GetSection(HgvMateOptions.SectionName).Bind(options);

        Assert.AreEqual("/custom/data", options.DataPath);
        Assert.AreEqual("sse", options.Transport);
    }
}
