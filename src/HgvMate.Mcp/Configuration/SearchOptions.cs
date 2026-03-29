namespace HgvMate.Mcp.Configuration;

public class SearchOptions
{
    public const string SectionName = "Search";
    public int MaxResults { get; set; } = 20;
    public int ChunkSize { get; set; } = 800;
    public int ChunkOverlap { get; set; } = 100;

    /// <summary>
    /// Number of threads ONNX Runtime uses for intra-op parallelism.
    /// 0 = auto-detect (half of available CPUs, clamped 1-16).
    /// </summary>
    public int OnnxThreadCount { get; set; } = 0;

    /// <summary>
    /// Number of chunks to embed in a single batch inference call.
    /// Higher values improve throughput but use more memory.
    /// </summary>
    public int OnnxBatchSize { get; set; } = 32;

    /// <summary>
    /// Force a specific ONNX execution provider instead of auto-detection.
    /// Supported: "auto" (default), "cuda", "openvino", "cpu".
    /// Auto tries CUDA → OpenVINO → CPU in order.
    /// </summary>
    public string OnnxProvider { get; set; } = "auto";
}
