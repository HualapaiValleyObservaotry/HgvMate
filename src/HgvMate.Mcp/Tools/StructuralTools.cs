using HgvMate.Mcp.Search;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace HgvMate.Mcp.Tools;

[McpServerToolType]
public class StructuralTools
{
    private readonly GitNexusService _gitNexus;

    public StructuralTools(GitNexusService gitNexus)
    {
        _gitNexus = gitNexus;
    }

    [McpServerTool(Name = "hgvmate_find_symbol")]
    [Description("Find a symbol (class, method, function) and see its callers, callees, and hierarchy.")]
    public async Task<string> FindSymbol(
        [Description("Symbol name to find (class, method, or function name)")] string name,
        [Description("Limit to specific repository (optional)")] string? repository = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Error: name is required.";

        try
        {
            return await _gitNexus.FindSymbolAsync(name, repository);
        }
        catch (Exception ex)
        {
            return $"Error finding symbol: {ex.Message}";
        }
    }

    [McpServerTool(Name = "hgvmate_get_references")]
    [Description("Find all references to a symbol — what calls or uses it.")]
    public async Task<string> GetReferences(
        [Description("Symbol name to find references for")] string name,
        [Description("Limit to specific repository (optional)")] string? repository = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Error: name is required.";

        try
        {
            return await _gitNexus.GetReferencesAsync(name, repository);
        }
        catch (Exception ex)
        {
            return $"Error getting references: {ex.Message}";
        }
    }

    [McpServerTool(Name = "hgvmate_get_call_chain")]
    [Description("Trace the full execution flow/call chain for a symbol.")]
    public async Task<string> GetCallChain(
        [Description("Symbol name to trace the call chain for")] string name,
        [Description("Limit to specific repository (optional)")] string? repository = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Error: name is required.";

        try
        {
            return await _gitNexus.GetCallChainAsync(name, repository);
        }
        catch (Exception ex)
        {
            return $"Error getting call chain: {ex.Message}";
        }
    }

    [McpServerTool(Name = "hgvmate_get_impact")]
    [Description("Get the blast radius — what would be affected if this symbol changed.")]
    public async Task<string> GetImpact(
        [Description("Symbol name to analyze impact for")] string name,
        [Description("Limit to specific repository (optional)")] string? repository = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Error: name is required.";

        try
        {
            return await _gitNexus.GetImpactAsync(name, repository);
        }
        catch (Exception ex)
        {
            return $"Error getting impact: {ex.Message}";
        }
    }
}
