using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HgvMate.Mcp;

/// <summary>
/// Shared ActivitySource and Meter for HgvMate custom telemetry.
/// </summary>
internal static class HgvMateDiagnostics
{
    public const string ServiceName = "HgvMate";

    public static readonly ActivitySource ActivitySource = new(ServiceName);
    public static readonly Meter Meter = new(ServiceName);

    // ── Counters ────────────────────────────────────────────────────────
    public static readonly Counter<long> RepoSyncTotal = Meter.CreateCounter<long>(
        "hgvmate.repo.sync.total", description: "Total repo sync operations");

    public static readonly Counter<long> RepoSyncErrors = Meter.CreateCounter<long>(
        "hgvmate.repo.sync.errors", description: "Repo sync failures");

    public static readonly Counter<long> IndexFilesTotal = Meter.CreateCounter<long>(
        "hgvmate.index.files.total", description: "Total files indexed");

    public static readonly Counter<long> IndexChunksTotal = Meter.CreateCounter<long>(
        "hgvmate.index.chunks.total", description: "Total chunks created");

    public static readonly Counter<long> SearchRequestsTotal = Meter.CreateCounter<long>(
        "hgvmate.search.requests.total", description: "Total search requests");

    public static readonly Counter<long> McpToolCalls = Meter.CreateCounter<long>(
        "hgvmate.mcp.tool_calls.total", description: "Total MCP tool invocations");

    // ── Histograms ──────────────────────────────────────────────────────
    public static readonly Histogram<double> RepoSyncDuration = Meter.CreateHistogram<double>(
        "hgvmate.repo.sync.duration", unit: "ms", description: "Repo sync duration");

    public static readonly Histogram<double> IndexDuration = Meter.CreateHistogram<double>(
        "hgvmate.index.duration", unit: "ms", description: "Index operation duration");

    public static readonly Histogram<double> SearchDuration = Meter.CreateHistogram<double>(
        "hgvmate.search.duration", unit: "ms", description: "Search request duration");

    // ── Gauges (via ObservableGauge) ────────────────────────────────────
    private static long _activeRepoCount;
    private static long _totalChunkCount;

    public static readonly ObservableGauge<long> ActiveRepos = Meter.CreateObservableGauge(
        "hgvmate.repos.active", () => Interlocked.Read(ref _activeRepoCount),
        description: "Number of active (enabled) repositories");

    public static readonly ObservableGauge<long> VectorChunks = Meter.CreateObservableGauge(
        "hgvmate.index.chunks.current", () => Interlocked.Read(ref _totalChunkCount),
        description: "Current number of vector chunks in store");

    public static void RecordToolCall(string toolName)
    {
        var tags = new TagList { { "tool", toolName } };
        McpToolCalls.Add(1, tags);
    }

    public static void SetActiveRepoCount(long count) =>
        Interlocked.Exchange(ref _activeRepoCount, count);

    public static void SetVectorChunkCount(long count) =>
        Interlocked.Exchange(ref _totalChunkCount, count);

    // ── Disk space (data path) ──────────────────────────────────────────
    private static string? _dataPath;

    public static void SetDataPath(string dataPath) => _dataPath = dataPath;

    public static readonly ObservableGauge<long> DiskAvailableBytes = Meter.CreateObservableGauge(
        "hgvmate.disk.available_bytes", () =>
        {
            if (_dataPath is null) return 0;
            try
            {
                var info = new DriveInfo(Path.GetPathRoot(_dataPath) ?? "/");
                return info.AvailableFreeSpace;
            }
            catch { return 0; }
        },
        unit: "By", description: "Available disk space on data volume");

    public static readonly ObservableGauge<long> DiskTotalBytes = Meter.CreateObservableGauge(
        "hgvmate.disk.total_bytes", () =>
        {
            if (_dataPath is null) return 0;
            try
            {
                var info = new DriveInfo(Path.GetPathRoot(_dataPath) ?? "/");
                return info.TotalSize;
            }
            catch { return 0; }
        },
        unit: "By", description: "Total disk space on data volume");
}
