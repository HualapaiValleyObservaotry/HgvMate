namespace HgvMate.Mcp.Configuration;

/// <summary>
/// Tracks initialization progress so the health endpoint can report startup status.
/// Registered as a singleton — shared between the warm-up service and the health endpoint.
/// </summary>
public sealed class StartupState
{
    private volatile bool _databaseReady;
    private volatile bool _vectorCacheReady;
    private volatile bool _onnxReady;

    public bool DatabaseReady => _databaseReady;
    public bool VectorCacheReady => _vectorCacheReady;
    public bool OnnxReady => _onnxReady;
    public bool IsReady => _databaseReady && _vectorCacheReady && _onnxReady;

    public void MarkDatabaseReady() => _databaseReady = true;
    public void MarkVectorCacheReady() => _vectorCacheReady = true;
    public void MarkOnnxReady() => _onnxReady = true;
}
