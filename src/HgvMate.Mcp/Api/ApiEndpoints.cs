using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Repos;
using HgvMate.Mcp.Search;
using HVO.Enterprise.Telemetry.Abstractions;
using HVO.Enterprise.Telemetry.Correlation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HgvMate.Mcp.Api;

public static class ApiEndpoints
{
    private static readonly DateTime _startTime = DateTime.UtcNow;

    public static WebApplication MapRestApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        MapHealthEndpoint(app);
        MapDiagnosticsEndpoint(app);
        MapRepositoryEndpoints(api);
        MapSearchEndpoints(api);
        MapStructuralEndpoints(api);

        return app;
    }

    // ── Health ──────────────────────────────────────────────────────────

    private static void MapHealthEndpoint(WebApplication app)
    {
        app.MapGet("/health", async (
            IRepoRegistry registry,
            VectorStore vectorStore,
            IOnnxEmbedder embedder,
            StartupState startupState,
            HgvMateOptions hgvMateOptions,
            RepoSyncOptions syncOptions) =>
        {
            if (!startupState.IsReady)
            {
                return Results.Json(new
                {
                    Status = "starting",
                    Uptime = (DateTime.UtcNow - _startTime).ToString(@"d\.hh\:mm\:ss"),
                    Warmup = new
                    {
                        Database = startupState.DatabaseReady,
                        VectorCache = startupState.VectorCacheReady,
                        Onnx = startupState.OnnxReady
                    }
                }, statusCode: 503);
            }

            var repos = await registry.GetAllAsync();
            var chunkCounts = vectorStore.GetChunkCounts();

            var repoStatuses = repos.Select(r => new
            {
                r.Name,
                r.Url,
                r.Branch,
                r.Enabled,
                LastSha = r.LastSha ?? "none",
                LastSynced = r.LastSynced ?? "never",
                IndexedChunks = chunkCounts.GetValueOrDefault(r.Name, 0),
                r.Source,
                SyncState = r.SyncState,
                LastError = r.LastError,
                LastErrorAt = r.LastErrorAt,
                FailedSyncCount = r.FailedSyncCount
            }).ToList();

            var synced = repos.Count(r => r.SyncState == SyncStates.Synced);
            var pending = repos.Count(r => r.SyncState is SyncStates.Pending or SyncStates.Syncing);
            var failedRepos = repos.Count(r => r.SyncState == SyncStates.Failed);
            var totalChunks = chunkCounts.Values.Sum();

            long? freeDiskMb = null;
            long? totalDiskMb = null;
            try
            {
                var dataRoot = Path.GetPathRoot(Path.GetFullPath(hgvMateOptions.DataPath)) ?? hgvMateOptions.DataPath;
                var driveInfo = new DriveInfo(dataRoot);
                freeDiskMb = driveInfo.AvailableFreeSpace / (1024 * 1024);
                totalDiskMb = driveInfo.TotalSize / (1024 * 1024);
            }
            catch { /* DriveInfo not available on all platforms */ }

            return Results.Ok(new
            {
                Status = failedRepos > 0 ? "degraded" : "healthy",
                Uptime = (DateTime.UtcNow - _startTime).ToString(@"d\.hh\:mm\:ss"),
                Transport = hgvMateOptions.Transport,
                Embedder = new
                {
                    Available = embedder.IsAvailable,
                    Model = "all-MiniLM-L6-v2",
                    embedder.ModelType,
                    embedder.SelectedModelFile,
                    embedder.ExecutionProvider,
                    embedder.ThreadCount,
                    embedder.BatchSize,
                    Dimensions = embedder.Dimensions,
                    embedder.CpuFeatures
                },
                Disk = new
                {
                    FreeMb = freeDiskMb,
                    TotalMb = totalDiskMb,
                    MinRequiredMb = syncOptions.MinFreeDiskSpaceMb
                },
                VectorCache = new
                {
                    Loaded = vectorStore.IsCacheLoaded,
                    Chunks = vectorStore.CachedChunkCount,
                    EstimatedSizeMb = Math.Round(vectorStore.EstimatedCacheSizeMb, 1)
                },
                Repositories = new
                {
                    Total = repos.Count,
                    Synced = synced,
                    Pending = pending,
                    Failed = failedRepos,
                    TotalIndexedChunks = totalChunks,
                    Details = repoStatuses
                }
            });
        })
        .WithTags("System")
        .WithSummary("System health check with sync status, disk space, and indexing info");
    }

    // ── Repository management ───────────────────────────────────────────

    private static void MapRepositoryEndpoints(RouteGroupBuilder api)
    {
        var repos = api.MapGroup("/repositories").WithTags("Repositories");

        repos.MapGet("/", async (IRepoRegistry registry) =>
        {
            var all = await registry.GetAllAsync();
            return TypedResults.Ok(all);
        })
        .WithSummary("List all registered repositories with their sync status");

        repos.MapPost("/", async (AddRepoRequest request, IRepoRegistry registry, RepoSyncService syncService, ILogger<RepoSyncService> logger) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "name is required." });
            if (request.Name.Length > 128)
                return Results.BadRequest(new { error = "name must be 128 characters or fewer." });
            if (string.IsNullOrWhiteSpace(request.Url))
                return Results.BadRequest(new { error = "url is required." });

            var validSources = new[] { "github", "azuredevops" };
            var source = request.Source ?? "github";
            if (!validSources.Contains(source, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"source must be one of: {string.Join(", ", validSources)}." });

            var existing = await registry.GetByNameAsync(request.Name);
            if (existing != null)
                return Results.Conflict(new { error = $"Repository '{request.Name}' already exists." });

            var existingUrl = await registry.GetByUrlAsync(request.Url);
            if (existingUrl != null)
                return Results.Conflict(new { error = $"A repository with the same URL is already registered as '{existingUrl.Name}' (branch: {existingUrl.Branch}). " +
                    "Adding the same repo with a different branch would create mostly duplicate search results." });

            var repo = await registry.AddAsync(request.Name, request.Url, request.Branch ?? "main", source.ToLowerInvariant(), addedBy: "rest-api");
            _ = Task.Run(async () =>
            {
                try { await syncService.SyncRepoAsync(repo); }
                catch (Exception ex) { logger.LogError(ex, "Background sync failed for '{Name}'.", repo.Name); }
            });
            return Results.Accepted($"/api/repositories/{repo.Name}/status", new
            {
                message = $"Repository '{repo.Name}' added. Sync initiated.",
                statusUrl = $"/api/repositories/{repo.Name}/status",
                repo.Name,
                repo.Url,
                repo.Branch,
                repo.Source,
                SyncState = repo.SyncState
            });
        })
        .RequireRateLimiting("mutating")
        .WithSummary("Add a repository to be indexed");

        repos.MapDelete("/{name}", async (string name, IRepoRegistry registry, RepoSyncService syncService) =>
        {
            var repo = await registry.GetByNameAsync(name);
            if (repo == null)
                return Results.NotFound(new { error = $"Repository '{name}' not found." });

            try
            {
                await syncService.DeleteRepoCloneAsync(name);
            }
            catch (IOException) { /* clone may still be in progress */ }
            await registry.RemoveAsync(name);
            return Results.Ok(new { message = $"Repository '{name}' removed." });
        })
        .RequireRateLimiting("mutating")
        .WithSummary("Remove a repository and delete its cloned data");

        repos.MapPost("/{name}/reindex", async (string name, bool? force, IRepoRegistry registry, RepoSyncService syncService, ILogger<RepoSyncService> logger) =>
        {
            var repo = await registry.GetByNameAsync(name);
            if (repo == null)
                return Results.NotFound(new { error = $"Repository '{name}' not found." });

            var isForce = force == true;
            _ = Task.Run(async () =>
            {
                try { await syncService.SyncRepoAsync(repo, isForce); }
                catch (Exception ex) { logger.LogError(ex, "Background reindex failed for '{Name}'.", name); }
            });
            return Results.Accepted(value: new { message = $"Reindex triggered for '{name}'{(isForce ? " (force)" : "")}." });
        })
        .RequireRateLimiting("mutating")
        .WithSummary("Trigger reindex for a specific repository. Use ?force=true to re-embed even if unchanged.");

        repos.MapPost("/reindex", async (bool? force, IRepoRegistry registry, RepoSyncService syncService, ILogger<RepoSyncService> logger) =>
        {
            var isForce = force == true;
            _ = Task.Run(async () =>
            {
                try { await syncService.SyncAllAsync(isForce); }
                catch (Exception ex) { logger.LogError(ex, "Background reindex-all failed."); }
            });
            return TypedResults.Ok(new { message = $"Reindex triggered for all repositories{(isForce ? " (force)" : "")}." });
        })
        .RequireRateLimiting("mutating")
        .WithSummary("Trigger reindex for all repositories. Use ?force=true to re-embed even if unchanged.");

        repos.MapGet("/{name}/status", async (string name, IRepoRegistry registry) =>
        {
            var repo = await registry.GetByNameAsync(name);
            if (repo == null)
                return Results.NotFound(new { error = $"Repository '{name}' not found." });

            return Results.Ok(new
            {
                repo.Name,
                repo.Enabled,
                LastSha = repo.LastSha ?? "none",
                LastSynced = repo.LastSynced ?? "never",
                repo.Source,
                SyncState = repo.SyncState,
                LastError = repo.LastError,
                LastErrorAt = repo.LastErrorAt,
                FailedSyncCount = repo.FailedSyncCount
            });
        })
        .WithSummary("Get index status for a specific repository");

        api.MapGet("/status", async (IRepoRegistry registry) =>
        {
            var repos = await registry.GetAllAsync();
            var statuses = repos.Select(r => new
            {
                r.Name,
                r.Enabled,
                LastSha = r.LastSha ?? "none",
                LastSynced = r.LastSynced ?? "never",
                r.Source,
                SyncState = r.SyncState,
                LastError = r.LastError,
                LastErrorAt = r.LastErrorAt,
                FailedSyncCount = r.FailedSyncCount
            });
            return TypedResults.Ok(statuses);
        })
        .WithTags("Repositories")
        .WithSummary("Get index status for all repositories");
    }

    // ── Search & file access ────────────────────────────────────────────

    private static void MapSearchEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/search", async (string query, string? repository, HybridSearchService search) =>
        {
            if (string.IsNullOrWhiteSpace(query))
                return Results.BadRequest(new { error = "query parameter is required." });

            var results = await search.SearchAsync(query, repository);
            return Results.Ok(results);
        })
        .WithTags("Search")
        .WithSummary("Search source code using text and semantic search");

        api.MapGet("/repositories/{repository}/files/{*path}", async (string repository, string path, SourceCodeReader reader) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path is required." });

            try
            {
                var content = await reader.GetFileAsync(repository, path);
                return Results.Ok(new { repository, path, content });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        })
        .WithTags("Search")
        .WithSummary("Read a source file from a cloned repository");
    }

    // ── Structural analysis ─────────────────────────────────────────────

    private static void MapStructuralEndpoints(RouteGroupBuilder api)
    {
        var structural = api.MapGroup("/").WithTags("Structural Analysis");

        structural.MapGet("/symbols/{name}", async (string name, string? repository, GitNexusService gitNexus) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "name is required." });

            var result = await gitNexus.FindSymbolAsync(name, repository);
            return Results.Ok(new { symbol = name, repository, result });
        })
        .WithSummary("Find a symbol and see its callers, callees, and hierarchy");

        structural.MapGet("/references/{name}", async (string name, string? repository, GitNexusService gitNexus) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "name is required." });

            var result = await gitNexus.GetReferencesAsync(name, repository);
            return Results.Ok(new { symbol = name, repository, result });
        })
        .WithSummary("Find all references to a symbol");

        structural.MapGet("/callchain/{name}", async (string name, string? repository, GitNexusService gitNexus) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "name is required." });

            var result = await gitNexus.GetCallChainAsync(name, repository);
            return Results.Ok(new { symbol = name, repository, result });
        })
        .WithSummary("Trace the full execution flow for a symbol");

        structural.MapGet("/impact/{name}", async (string name, string? repository, GitNexusService gitNexus) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "name is required." });

            var result = await gitNexus.GetImpactAsync(name, repository);
            return Results.Ok(new { symbol = name, repository, result });
        })
        .WithSummary("Get the blast radius for a symbol");
    }

    // ── Diagnostics (telemetry statistics) ───────────────────────────────

    private static void MapDiagnosticsEndpoint(WebApplication app)
    {
        app.MapGet("/diagnostics", (ITelemetryService telemetry) =>
        {
            var stats = telemetry.Statistics;
            var snapshot = stats.GetSnapshot();

            return Results.Ok(new
            {
                Uptime = snapshot.Uptime.ToString(@"d\.hh\:mm\:ss"),
                ActivitiesCreated = snapshot.ActivitiesCreated,
                ActivitiesCompleted = snapshot.ActivitiesCompleted,
                ActiveActivities = snapshot.ActiveActivities,
                ExceptionsTracked = snapshot.ExceptionsTracked,
                EventsRecorded = snapshot.EventsRecorded,
                MetricsRecorded = snapshot.MetricsRecorded,
                QueueDepth = snapshot.QueueDepth,
                ItemsProcessed = snapshot.ItemsProcessed,
                ItemsDropped = snapshot.ItemsDropped,
                ErrorRate = $"{snapshot.CurrentErrorRate:F2}/min",
                Throughput = $"{snapshot.CurrentThroughput:F2}/sec",
                CorrelationId = CorrelationContext.Current
            });
        })
        .WithTags("System")
        .WithSummary("Live telemetry statistics — activities, errors, queue depth, throughput");
    }
}

public record AddRepoRequest(
    string Name,
    string Url,
    string? Branch = "main",
    string? Source = "github"
);
