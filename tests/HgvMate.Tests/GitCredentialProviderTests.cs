using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Repos;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

[TestClass]
public sealed class GitCredentialProviderTests
{
    [TestMethod]
    public void GetToken_GitHub_ReturnsGitHubToken()
    {
        var options = new CredentialOptions { GitHubToken = "ghp_testtoken" };
        var provider = new GitCredentialProvider(options, NullLogger<GitCredentialProvider>.Instance);
        Assert.AreEqual("ghp_testtoken", provider.GetToken("github"));
    }

    [TestMethod]
    public void GetToken_AzureDevOps_ReturnsAzureDevOpsPat()
    {
        var options = new CredentialOptions { AzureDevOpsPat = "myazurepat" };
        var provider = new GitCredentialProvider(options, NullLogger<GitCredentialProvider>.Instance);
        Assert.AreEqual("myazurepat", provider.GetToken("azuredevops"));
    }

    [TestMethod]
    public void GetToken_Unknown_ReturnsNull()
    {
        var options = new CredentialOptions();
        var provider = new GitCredentialProvider(options, NullLogger<GitCredentialProvider>.Instance);
        Assert.IsNull(provider.GetToken("unknown"));
    }

    [TestMethod]
    public void BuildAuthenticatedUrl_GitHub_InjectsToken()
    {
        var options = new CredentialOptions { GitHubToken = "mytoken" };
        var provider = new GitCredentialProvider(options, NullLogger<GitCredentialProvider>.Instance);
        var url = provider.BuildAuthenticatedUrl("https://github.com/org/repo.git", "github");
        Assert.AreEqual("https://mytoken@github.com/org/repo.git", url);
    }

    [TestMethod]
    public void BuildAuthenticatedUrl_AzureDevOps_InjectsPat()
    {
        var options = new CredentialOptions { AzureDevOpsPat = "mypat" };
        var provider = new GitCredentialProvider(options, NullLogger<GitCredentialProvider>.Instance);
        var url = provider.BuildAuthenticatedUrl("https://dev.azure.com/org/project/_git/repo", "azuredevops");
        Assert.AreEqual("https://:mypat@dev.azure.com/org/project/_git/repo", url);
    }

    [TestMethod]
    public void BuildAuthenticatedUrl_NoToken_ReturnsOriginalUrl()
    {
        // Set empty string (not null) to prevent env var fallback
        var options = new CredentialOptions { GitHubToken = string.Empty };
        var provider = new GitCredentialProvider(options, NullLogger<GitCredentialProvider>.Instance);
        const string url = "https://github.com/org/repo.git";
        Assert.AreEqual(url, provider.BuildAuthenticatedUrl(url, "github"));
    }

    [TestMethod]
    public void BuildAuthenticatedUrl_InvalidUrl_ReturnsOriginalUrl()
    {
        var options = new CredentialOptions { GitHubToken = "mytoken" };
        var provider = new GitCredentialProvider(options, NullLogger<GitCredentialProvider>.Instance);
        const string url = "not-a-valid-url";
        Assert.AreEqual(url, provider.BuildAuthenticatedUrl(url, "github"));
    }
}
