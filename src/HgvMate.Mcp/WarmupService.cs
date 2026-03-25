using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Data;
using HgvMate.Mcp.Search;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp;

/// <summary>
/// Initializes data stores in the background so Kestrel starts accepting requests immediately.
/// Completes DB schema, vector cache, and ONNX model warmup, updating <see cref="StartupState"/>
/// so the health endpoint can report readiness.
/// </summary>
public sealed class WarmupService : BackgroundService
{
    private readonly DatabaseInitializer _dbInit;
    private readonly VectorStore _vectorStore;
    private readonly IOnnxEmbedder _embedder;
    private readonly StartupState _startupState;
    private readonly ILogger<WarmupService> _logger;

    public WarmupService(
        DatabaseInitializer dbInit,
        VectorStore vectorStore,
        IOnnxEmbedder embedder,
        StartupState startupState,
        ILogger<WarmupService> logger)
    {
        _dbInit = dbInit;
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
            _logger.LogInformation("Warmup: initializing database schema...");
            await _dbInit.InitializeAsync();
            _startupState.MarkDatabaseReady();
            _logger.LogInformation("Warmup: database ready.");

            _logger.LogInformation("Warmup: loading vector cache...");
            await _vectorStore.EnsureSchemaAsync();
            _startupState.MarkVectorCacheReady();
            _logger.LogInformation("Warmup: vector cache ready ({Chunks} chunks).", _vectorStore.CachedChunkCount);

            // ONNX model loads in constructor, just verify it's available
            _startupState.MarkOnnxReady();
            _logger.LogInformation("Warmup: ONNX embedder available={Available}.", _embedder.IsAvailable);

            _logger.LogInformation("Warmup: all services ready.");
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Warmup: initialization failed. The server is running but some features may be unavailable.");
        }
    }
}
