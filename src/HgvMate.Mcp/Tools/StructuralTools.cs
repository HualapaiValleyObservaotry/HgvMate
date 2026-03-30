using System.Diagnostics;
using HgvMate.Mcp.Data;
using HgvMate.Mcp.Search;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace HgvMate.Mcp.Tools;

[McpServerToolType]
public class StructuralTools
{
    private readonly GitNexusService _gitNexus;
    private readonly ToolUsageLogger _usageLogger;

    public StructuralTools(GitNexusService gitNexus, ToolUsageLogger usageLogger)
    {
        _gitNexus = gitNexus;
        _usageLogger = usageLogger;
    }

    [McpServerTool(Name = "hgvmate_find_symbol")]
    [Description("Find a symbol (class, method, function) and see its callers, callees, and hierarchy.")]
    public async Task<string> FindSymbol(
        [Description("Symbol name to find (class, method, or function name)")] string name,
        [Description("Limit to specific repository (optional)")] string? repository = null)
    {
        HgvMateDiagnostics.RecordToolCall("find_symbol");
        var sw = Stopwatch.StartNew();
        string? error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Error: name is required.";

            try
            {
                return await _gitNexus.FindSymbolAsync(name, repository);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return $"Error finding symbol: {ex.Message}";
            }
        }
        finally
        {
            _usageLogger.Log("hgvmate_find_symbol", new { name, repository }, sw.Elapsed.TotalMilliseconds, error: error);
        }
    }

    [McpServerTool(Name = "hgvmate_get_references")]
    [Description("Find all references to a symbol — what calls or uses it.")]
    public async Task<string> GetReferences(
        [Description("Symbol name to find references for")] string name,
        [Description("Limit to specific repository (optional)")] string? repository = null)
    {
        HgvMateDiagnostics.RecordToolCall("get_references");
        var sw = Stopwatch.StartNew();
        string? error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Error: name is required.";

            try
            {
                return await _gitNexus.GetReferencesAsync(name, repository);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return $"Error getting references: {ex.Message}";
            }
        }
        finally
        {
            _usageLogger.Log("hgvmate_get_references", new { name, repository }, sw.Elapsed.TotalMilliseconds, error: error);
        }
    }

    [McpServerTool(Name = "hgvmate_get_call_chain")]
    [Description("Trace the full execution flow/call chain for a symbol.")]
    public async Task<string> GetCallChain(
        [Description("Symbol name to trace the call chain for")] string name,
        [Description("Limit to specific repository (optional)")] string? repository = null)
    {
        HgvMateDiagnostics.RecordToolCall("get_call_chain");
        var sw = Stopwatch.StartNew();
        string? error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Error: name is required.";

            try
            {
                return await _gitNexus.GetCallChainAsync(name, repository);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return $"Error getting call chain: {ex.Message}";
            }
        }
        finally
        {
            _usageLogger.Log("hgvmate_get_call_chain", new { name, repository }, sw.Elapsed.TotalMilliseconds, error: error);
        }
    }

    [McpServerTool(Name = "hgvmate_get_impact")]
    [Description("Get the blast radius — what would be affected if this symbol changed.")]
    public async Task<string> GetImpact(
        [Description("Symbol name to analyze impact for")] string name,
        [Description("Limit to specific repository (optional)")] string? repository = null)
    {
        HgvMateDiagnostics.RecordToolCall("get_impact");
        var sw = Stopwatch.StartNew();
        string? error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Error: name is required.";

            try
            {
                return await _gitNexus.GetImpactAsync(name, repository);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return $"Error getting impact: {ex.Message}";
            }
        }
        finally
        {
            _usageLogger.Log("hgvmate_get_impact", new { name, repository }, sw.Elapsed.TotalMilliseconds, error: error);
        }
    }
}
