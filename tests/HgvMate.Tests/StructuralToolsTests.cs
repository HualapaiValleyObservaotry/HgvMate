using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Search;
using HgvMate.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

[TestClass]
public sealed class StructuralToolsTests
{
    [TestMethod]
    public async Task FindSymbol_EmptyName_ReturnsError()
    {
        var tools = new StructuralTools(CreateFakeGitNexusService());
        var result = await tools.FindSymbol("");
        StringAssert.Contains(result, "Error");
    }

    [TestMethod]
    public async Task GetReferences_EmptyName_ReturnsError()
    {
        var tools = new StructuralTools(CreateFakeGitNexusService());
        var result = await tools.GetReferences("");
        StringAssert.Contains(result, "Error");
    }

    [TestMethod]
    public async Task GetCallChain_EmptyName_ReturnsError()
    {
        var tools = new StructuralTools(CreateFakeGitNexusService());
        var result = await tools.GetCallChain("");
        StringAssert.Contains(result, "Error");
    }

    [TestMethod]
    public async Task GetImpact_EmptyName_ReturnsError()
    {
        var tools = new StructuralTools(CreateFakeGitNexusService());
        var result = await tools.GetImpact("");
        StringAssert.Contains(result, "Error");
    }

    [TestMethod]
    public async Task FindSymbol_ValidName_NoIndexedRepo_ReturnsNotFound()
    {
        var tools = new StructuralTools(CreateFakeGitNexusService());
        var result = await tools.FindSymbol("MyClass");
        // No repos exist so the result should indicate nothing was found
        StringAssert.Contains(result, "not found");
    }

    [TestMethod]
    public async Task GetReferences_ValidName_NoIndexedRepo_ReturnsNotFound()
    {
        var tools = new StructuralTools(CreateFakeGitNexusService());
        var result = await tools.GetReferences("MyMethod");
        StringAssert.Contains(result, "No references found");
    }

    [TestMethod]
    public async Task GetCallChain_ValidName_NoIndexedRepo_ReturnsNotFound()
    {
        var tools = new StructuralTools(CreateFakeGitNexusService());
        var result = await tools.GetCallChain("Execute");
        StringAssert.Contains(result, "No call chain found");
    }

    [TestMethod]
    public async Task GetImpact_ValidName_NoIndexedRepo_ReturnsNotFound()
    {
        var tools = new StructuralTools(CreateFakeGitNexusService());
        var result = await tools.GetImpact("Process");
        StringAssert.Contains(result, "No impact found");
    }

    [TestMethod]
    public async Task FindSymbol_WhitespaceOnlyName_ReturnsError()
    {
        var tools = new StructuralTools(CreateFakeGitNexusService());
        var result = await tools.FindSymbol("   ");
        StringAssert.Contains(result, "Error");
    }

    private static GitNexusService CreateFakeGitNexusService()
    {
        var hgvOptions = new HgvMateOptions { DataPath = Path.GetTempPath() };
        var syncOptions = new RepoSyncOptions();
        return new GitNexusService(
            hgvOptions, syncOptions,
            NullLogger<GitNexusService>.Instance);
    }
}
