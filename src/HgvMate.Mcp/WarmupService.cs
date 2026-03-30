using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Search;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp;

/// <summary>
/// Initializes data stores in the background so Kestrel starts accepting requests immediately.
/// Loads the vector cache and warms up the ONNX model, updating <see cref="StartupState"/>
/// so the health endpoint can report readiness.
/// </summary>
public sealed class WarmupService : BackgroundService
{
    private readonly VectorStore _vectorStore;
    private readonly IOnnxEmbedder _embedder;
    private readonly StartupState _startupState;
    private readonly ILogger<WarmupService> _logger;

    public WarmupService(
        VectorStore vectorStore,
        IOnnxEmbedder embedder,
        StartupState startupState,
        ILogger<WarmupService> logger)
    {
        _vectorStore = vectorStore;
        _embedder = embedder;
        _startupState = startupState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield immediately so the host can finish starting (Kestrel begins accepting requests)
        await Task.Yield();

        try
        {
            // No database initialization needed — repo metadata is stored as JSON files
            _startupState.MarkDatabaseReady();

            _logger.LogInformation("Warmup: loading vector cache...");
            await _vectorStore.LoadAsync();
            _startupState.MarkVectorCacheReady();
            HgvMateDiagnostics.SetVectorChunkCount(_vectorStore.CachedChunkCount);
            _logger.LogInformation("Warmup: vector cache ready ({Chunks} chunks).", _vectorStore.CachedChunkCount);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Warmup: vector cache initialization failed. Semantic search may be unavailable.");
        }

        try
        {
            // ONNX model loads in constructor, only mark ready if it is actually available
            if (_embedder.IsAvailable)
            {
                _startupState.MarkOnnxReady();
                _logger.LogInformation("Warmup: ONNX embedder available={Available}.", _embedder.IsAvailable);
            }
            else
            {
                _logger.LogWarning("Warmup: ONNX embedder is not available. Semantic search may be unavailable.");
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Warmup: ONNX initialization failed. Semantic search may be unavailable.");
        }

        if (_startupState.IsReady)
            _logger.LogInformation("Warmup: all services ready.");
        else
            _logger.LogWarning("Warmup: completed with partial failures. Database={Database}, VectorCache={VectorCache}, Onnx={Onnx}.",
                _startupState.DatabaseReady, _startupState.VectorCacheReady, _startupState.OnnxReady);
    }

}
