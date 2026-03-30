using HgvMate.Mcp.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests.Helpers;

/// <summary>
/// Creates a <see cref="ToolUsageLogger"/> backed by a temp directory. The logger is
/// constructed but NOT initialized — call <see cref="ToolUsageLogger.InitializeAsync"/> to
/// create the schema and start the background consumer.
/// </summary>
internal static class TestToolUsageLogger
{
    public static ToolUsageLogger Create(string? dataPath = null)
    {
        dataPath ??= Path.Combine(Path.GetTempPath(), "hgvmate_test_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dataPath);
        return new ToolUsageLogger(dataPath, NullLogger<ToolUsageLogger>.Instance);
    }
}
