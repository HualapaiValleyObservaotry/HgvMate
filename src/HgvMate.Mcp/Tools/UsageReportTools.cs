using System.Text;
using HgvMate.Mcp.Data;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace HgvMate.Mcp.Tools;

[McpServerToolType]
public class UsageReportTools
{
    private readonly ToolUsageLogger _usageLogger;

    public UsageReportTools(ToolUsageLogger usageLogger)
    {
        _usageLogger = usageLogger;
    }

    [McpServerTool(Name = "hgvmate_usage_report")]
    [Description("Get tool usage analytics — top tools, repeated cross-repo searches, common tool sequences, and error rates. " +
                 "Optionally filter by date range (ISO 8601).")]
    public async Task<string> UsageReport(
        [Description("Report type: 'summary' (default), 'repeated_searches', 'sequences', 'errors', or 'all'")] string report = "summary",
        [Description("Start date for the report (ISO 8601, optional)")] string? from = null,
        [Description("End date for the report (ISO 8601, optional)")] string? to = null)
    {
        HgvMateDiagnostics.RecordToolCall("usage_report");

        DateTime? fromDate = null, toDate = null;
        if (!string.IsNullOrWhiteSpace(from) && !DateTime.TryParse(from, out var fd))
            return "Error: 'from' must be a valid ISO 8601 date.";
        else if (!string.IsNullOrWhiteSpace(from))
            fromDate = DateTime.Parse(from);

        if (!string.IsNullOrWhiteSpace(to) && !DateTime.TryParse(to, out var td))
            return "Error: 'to' must be a valid ISO 8601 date.";
        else if (!string.IsNullOrWhiteSpace(to))
            toDate = DateTime.Parse(to);

        var validReports = new[] { "summary", "repeated_searches", "sequences", "errors", "all" };
        if (!validReports.Contains(report, StringComparer.OrdinalIgnoreCase))
            return $"Error: report must be one of: {string.Join(", ", validReports)}.";

        try
        {
            var sb = new StringBuilder();
            var isAll = report.Equals("all", StringComparison.OrdinalIgnoreCase);

            if (isAll || report.Equals("summary", StringComparison.OrdinalIgnoreCase))
            {
                var summaries = await _usageLogger.GetToolSummariesAsync(fromDate, toDate);
                sb.AppendLine("## Tool Usage Summary");
                if (!summaries.Any())
                {
                    sb.AppendLine("No usage data recorded.");
                }
                else
                {
                    sb.AppendLine($"{"Tool",-40} {"Calls",8} {"Avg ms",10} {"Max ms",10} {"Errors",8}");
                    sb.AppendLine(new string('-', 78));
                    foreach (var s in summaries)
                        sb.AppendLine($"{s.ToolName,-40} {s.Calls,8} {s.AvgDurationMs,10:F1} {s.MaxDurationMs,10:F1} {s.ErrorCount,8}");
                }
                sb.AppendLine();
            }

            if (isAll || report.Equals("repeated_searches", StringComparison.OrdinalIgnoreCase))
            {
                var repeated = await _usageLogger.GetRepeatedSearchesAsync(fromDate, toDate);
                sb.AppendLine("## Repeated Cross-Repo Searches");
                if (!repeated.Any())
                {
                    sb.AppendLine("No repeated cross-repo searches detected.");
                }
                else
                {
                    foreach (var r in repeated)
                        sb.AppendLine($"- Session '{r.SessionId}': query='{r.Query}' searched across {r.RepoCount} repos");
                }
                sb.AppendLine();
            }

            if (isAll || report.Equals("sequences", StringComparison.OrdinalIgnoreCase))
            {
                var sequences = await _usageLogger.GetToolSequencesAsync(fromDate, toDate);
                sb.AppendLine("## Common Tool Sequences");
                if (!sequences.Any())
                {
                    sb.AppendLine("No tool sequences detected.");
                }
                else
                {
                    foreach (var seq in sequences)
                        sb.AppendLine($"- {seq.FromTool} → {seq.ToTool}: {seq.Count} times");
                }
                sb.AppendLine();
            }

            if (isAll || report.Equals("errors", StringComparison.OrdinalIgnoreCase))
            {
                var errors = await _usageLogger.GetErrorRatesAsync(fromDate, toDate);
                sb.AppendLine("## Error Rates by Tool");
                if (!errors.Any())
                {
                    sb.AppendLine("No usage data recorded.");
                }
                else
                {
                    sb.AppendLine($"{"Tool",-40} {"Total",8} {"Errors",8} {"Rate",8}");
                    sb.AppendLine(new string('-', 66));
                    foreach (var e in errors)
                        sb.AppendLine($"{e.ToolName,-40} {e.TotalCalls,8} {e.Errors,8} {e.ErrorPercent,7:F1}%");
                }
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Error generating usage report: {ex.Message}";
        }
    }
}
