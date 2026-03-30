using HgvMate.Mcp.Configuration;

namespace HgvMate.Tests;

[TestClass]
public sealed class BuildInfoTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void Version_IsNotNullOrEmpty()
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(BuildInfo.Version));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void GitSha_HasFallbackValue()
    {
        // In test/local builds, GitSha falls back to "dev"
        Assert.IsFalse(string.IsNullOrWhiteSpace(BuildInfo.GitSha));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void BuildDate_HasValue()
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(BuildInfo.BuildDate));
        Assert.AreNotEqual("unknown", BuildInfo.BuildDate);
    }
}
