using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Data;
using HgvMate.Mcp.Repos;
using HgvMate.Mcp.Search;
using ModelContextProtocol.Server;

namespace HgvMate.Mcp.Tools;

[McpServerToolType]
public class ServerInfoTools
{
    private static readonly DateTime _startTime = DateTime.UtcNow;

    private readonly IRepoRegistry _registry;
    private readonly IOnnxEmbedder _embedder;
    private readonly HgvMateOptions _options;
    private readonly ToolUsageLogger _usageLogger;

    public ServerInfoTools(IRepoRegistry registry, IOnnxEmbedder embedder, HgvMateOptions options, ToolUsageLogger usageLogger)
    {
        _registry = registry;
        _embedder = embedder;
        _options = options;
        _usageLogger = usageLogger;
    }

    [McpServerTool(Name = "hgvmate_server_info")]
    [Description("Get HgvMate server identity, version, uptime, and capability summary. Lightweight — no database queries.")]
    public async Task<string> ServerInfo()
    {
        HgvMateDiagnostics.RecordToolCall("server_info");
        var sw = Stopwatch.StartNew();
        string? error = null;
        try
        {
            var repos = await _registry.GetAllAsync();

            var sb = new StringBuilder();
            sb.AppendLine("## HgvMate Server Info");
            sb.AppendLine();
            sb.AppendLine($"- **Version:** {BuildInfo.Version}");
            sb.AppendLine($"- **Git SHA:** {BuildInfo.GitSha}");
            sb.AppendLine($"- **Build Date:** {BuildInfo.BuildDate}");
            sb.AppendLine($"- **Uptime:** {(DateTime.UtcNow - _startTime):d\\.hh\\:mm\\:ss}");
            sb.AppendLine($"- **Transport:** {_options.Transport}");
            sb.AppendLine();
            sb.AppendLine("### Capabilities");
            sb.AppendLine($"- **Repository Count:** {repos.Count}");
            sb.AppendLine($"- **Vector Search:** {_embedder.IsAvailable}");
            sb.AppendLine($"- **Structural Analysis:** true");
            sb.AppendLine($"- **Embedding Model:** all-MiniLM-L6-v2");
            sb.AppendLine($"- **Execution Provider:** {_embedder.ExecutionProvider}");
            sb.AppendLine();
            sb.AppendLine("### Endpoints");
            sb.AppendLine("- Health: `/health`");
            sb.AppendLine("- REST API: `/api/*`");
            sb.AppendLine("- MCP: `/mcp`");
            sb.AppendLine("- Diagnostics: `/diagnostics`");

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            throw;
        }
        finally
        {
            _usageLogger.Log("hgvmate_server_info", new { }, sw.Elapsed.TotalMilliseconds, error: error);
        }
    }
}
