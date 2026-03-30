using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Data;
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
        MapUsageEndpoints(api);
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
                return Results.Problem(detail: "name is required.", statusCode: StatusCodes.Status400BadRequest);
            if (request.Name.Length > 128)
                return Results.Problem(detail: "name must be 128 characters or fewer.", statusCode: StatusCodes.Status400BadRequest);
            if (string.IsNullOrWhiteSpace(request.Url))
                return Results.Problem(detail: "url is required.", statusCode: StatusCodes.Status400BadRequest);

            var validSources = new[] { "github", "azuredevops" };
            var source = request.Source ?? "github";
            if (!validSources.Contains(source, StringComparer.OrdinalIgnoreCase))
                return Results.Problem(
                    detail: $"source must be one of: {string.Join(", ", validSources)}.",
                    statusCode: StatusCodes.Status400BadRequest);

            var existing = await registry.GetByNameAsync(request.Name);
            if (existing != null)
                return Results.Problem(
                    detail: $"Repository '{request.Name}' already exists.",
                    statusCode: StatusCodes.Status409Conflict);

            var existingUrl = await registry.GetByUrlAsync(request.Url);
            if (existingUrl != null)
                return Results.Problem(
                    detail: $"A repository with the same URL is already registered as '{existingUrl.Name}' (branch: {existingUrl.Branch}). " +
                            "Adding the same repo with a different branch would create mostly duplicate search results.",
                    statusCode: StatusCodes.Status409Conflict);

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
                return Results.Problem(
                    detail: $"Repository '{name}' not found.",
                    statusCode: StatusCodes.Status404NotFound);

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

        repos.MapPost("/{name}/reindex", async (string name, bool? force, string? scope, IRepoRegistry registry, RepoSyncService syncService, ILogger<RepoSyncService> logger) =>
        {
            var repo = await registry.GetByNameAsync(name);
            if (repo == null)
                return Results.Problem(
                    detail: $"Repository '{name}' not found.",
                    statusCode: StatusCodes.Status404NotFound);

            var reindexScope = scope?.ToLowerInvariant() ?? "all";
            var validScopes = new[] { "all", "vectors", "gitnexus" };
            if (!validScopes.Contains(reindexScope))
                return Results.Problem(
                    detail: $"scope must be one of: {string.Join(", ", validScopes)}.",
                    statusCode: StatusCodes.Status400BadRequest);

            if (reindexScope != "all" && !syncService.IsRepoCloned(name))
                return Results.Problem(
                    detail: $"Repository '{name}' is not cloned yet. Run a full reindex first.",
                    statusCode: StatusCodes.Status409Conflict);

            var isForce = force == true;
            _ = Task.Run(async () =>
            {
                try
                {
                    switch (reindexScope)
                    {
                        case "vectors":
                            await syncService.ReindexVectorsAsync(repo);
                            break;
                        case "gitnexus":
                            await syncService.ReindexGitNexusAsync(repo);
                            break;
                        default:
                            await syncService.SyncRepoAsync(repo, isForce);
                            break;
                    }
                }
                catch (Exception ex) { logger.LogError(ex, "Background reindex failed for '{Name}'.", name); }
            });
            return Results.Accepted(value: new { message = $"Reindex triggered for '{name}' (scope: {reindexScope}){(isForce && reindexScope == "all" ? ", force" : "")}." });
        })
        .RequireRateLimiting("mutating")
        .WithSummary("Trigger reindex for a specific repository. " +
                     "Optional: ?force=true to re-embed even if unchanged, ?scope=all|vectors|gitnexus to limit reindex scope.");

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
                return Results.Problem(
                    detail: $"Repository '{name}' not found.",
                    statusCode: StatusCodes.Status404NotFound);

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
        api.MapGet("/search", async (
            string query,
            string? repository,
            string? includeExtensions,
            string? excludePatterns,
            HybridSearchService search) =>
        {
            if (string.IsNullOrWhiteSpace(query))
                return Results.Problem(detail: "query parameter is required.", statusCode: StatusCodes.Status400BadRequest);

            var extensions = string.IsNullOrWhiteSpace(includeExtensions)
                ? null
                : includeExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var excludes = string.IsNullOrWhiteSpace(excludePatterns)
                ? null
                : excludePatterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var results = await search.SearchAsync(query, repository, extensions, excludes);
            return Results.Ok(results);
        })
        .WithTags("Search")
        .WithSummary("Search source code using text and semantic search. " +
                     "Optional: includeExtensions (.cs,.ts) and excludePatterns (*.min.js,package-lock.json).");

        api.MapGet("/repositories/{repository}/files/{*path}", async (string repository, string path, SourceCodeReader reader) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.Problem(detail: "path is required.", statusCode: StatusCodes.Status400BadRequest);

            try
            {
                var content = await reader.GetFileAsync(repository, path);
                return Results.Ok(new { repository, path, content });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (FileNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
        })
        .WithTags("Search")
        .WithSummary("Read a source file from a cloned repository");

        api.MapGet("/repositories/{repository}/tree", async (
            string repository,
            string? path,
            int? depth,
            SourceCodeReader reader) =>
        {
            if (depth.HasValue && (depth < 1 || depth > 10))
                return Results.Problem(detail: "depth must be between 1 and 10.", statusCode: StatusCodes.Status400BadRequest);

            try
            {
                var entries = await reader.GetRepoTreeAsync(repository, path, depth ?? 2);
                return Results.Ok(new { repository, path, depth = depth ?? 2, entries });
            }
            catch (DirectoryNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        })
        .WithTags("Search")
        .WithSummary("List the file/folder tree of a repository up to the given depth (default: 2). " +
                     "Folders deeper than the limit are collapsed with a trailing '/'. " +
                     "Optional: path (subtree prefix), depth (1-10).");

        api.MapGet("/repositories/{repository}/find", async (
            string repository,
            string pattern,
            SourceCodeReader reader) =>
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return Results.Problem(detail: "pattern query parameter is required.", statusCode: StatusCodes.Status400BadRequest);

            try
            {
                var files = await reader.FindFilesAsync(repository, pattern);
                return Results.Ok(new { repository, pattern, count = files.Count, files });
            }
            catch (DirectoryNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
        })
        .WithTags("Search")
        .WithSummary("Find files matching a glob pattern in a repository (e.g. '*.csproj', '*Controller.cs').");

        api.MapGet("/repositories/{repository}/techstack", async (
            string repository,
            SourceCodeReader reader) =>
        {
            try
            {
                var content = await reader.GetFileAsync(repository, ".hgvmate/techstack.yml");
                return Results.Ok(new { repository, content });
            }
            catch (FileNotFoundException)
            {
                return Results.Problem(
                    detail: $"No .hgvmate/techstack.yml found for repository '{repository}'. " +
                            "Create and commit this file in the repository root to enable tech-stack awareness.",
                    statusCode: StatusCodes.Status404NotFound);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        })
        .WithTags("Search")
        .WithSummary("Return the '.hgvmate/techstack.yml' metadata file for the repository.");
    }

    // ── Structural analysis ─────────────────────────────────────────────

    private static void MapStructuralEndpoints(RouteGroupBuilder api)
    {
        var structural = api.MapGroup("/").WithTags("Structural Analysis");

        structural.MapGet("/symbols/{name}", async (string name, string? repository, GitNexusService gitNexus) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Results.Problem(detail: "name is required.", statusCode: StatusCodes.Status400BadRequest);

            var result = await gitNexus.FindSymbolAsync(name, repository);
            return Results.Ok(new { symbol = name, repository, result });
        })
        .WithSummary("Find a symbol and see its callers, callees, and hierarchy");

        structural.MapGet("/references/{name}", async (string name, string? repository, GitNexusService gitNexus) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Results.Problem(detail: "name is required.", statusCode: StatusCodes.Status400BadRequest);

            var result = await gitNexus.GetReferencesAsync(name, repository);
            return Results.Ok(new { symbol = name, repository, result });
        })
        .WithSummary("Find all references to a symbol");

        structural.MapGet("/callchain/{name}", async (string name, string? repository, GitNexusService gitNexus) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Results.Problem(detail: "name is required.", statusCode: StatusCodes.Status400BadRequest);

            var result = await gitNexus.GetCallChainAsync(name, repository);
            return Results.Ok(new { symbol = name, repository, result });
        })
        .WithSummary("Trace the full execution flow for a symbol");

        structural.MapGet("/impact/{name}", async (string name, string? repository, GitNexusService gitNexus) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Results.Problem(detail: "name is required.", statusCode: StatusCodes.Status400BadRequest);

            var result = await gitNexus.GetImpactAsync(name, repository);
            return Results.Ok(new { symbol = name, repository, result });
        })
        .WithSummary("Get the blast radius for a symbol");
    }

    // ── Usage Analytics ────────────────────────────────────────────────

    private static void MapUsageEndpoints(RouteGroupBuilder api)
    {
        var usage = api.MapGroup("/diagnostics/usage").WithTags("Diagnostics");

        usage.MapGet("/", async (ToolUsageLogger usageLogger, string? from, string? to) =>
        {
            DateTime? fromDate = null, toDate = null;
            if (!string.IsNullOrWhiteSpace(from))
            {
                if (!DateTime.TryParse(from, out var fd))
                    return Results.Problem(detail: "'from' must be a valid ISO 8601 date.", statusCode: StatusCodes.Status400BadRequest);
                fromDate = fd;
            }
            if (!string.IsNullOrWhiteSpace(to))
            {
                if (!DateTime.TryParse(to, out var td))
                    return Results.Problem(detail: "'to' must be a valid ISO 8601 date.", statusCode: StatusCodes.Status400BadRequest);
                toDate = td;
            }

            var summaries = await usageLogger.GetToolSummariesAsync(fromDate, toDate);
            var errorRates = await usageLogger.GetErrorRatesAsync(fromDate, toDate);

            return Results.Ok(new
            {
                tools = summaries.Select(s => new
                {
                    s.ToolName,
                    s.Calls,
                    s.AvgDurationMs,
                    s.MaxDurationMs,
                    s.ErrorCount
                }),
                errorRates = errorRates.Select(e => new
                {
                    e.ToolName,
                    e.TotalCalls,
                    e.Errors,
                    e.ErrorPercent
                })
            });
        })
        .WithSummary("Tool usage summary — call counts, durations, and error rates");

        usage.MapGet("/patterns", async (ToolUsageLogger usageLogger, string? from, string? to) =>
        {
            DateTime? fromDate = null, toDate = null;
            if (!string.IsNullOrWhiteSpace(from))
            {
                if (!DateTime.TryParse(from, out var fd))
                    return Results.Problem(detail: "'from' must be a valid ISO 8601 date.", statusCode: StatusCodes.Status400BadRequest);
                fromDate = fd;
            }
            if (!string.IsNullOrWhiteSpace(to))
            {
                if (!DateTime.TryParse(to, out var td))
                    return Results.Problem(detail: "'to' must be a valid ISO 8601 date.", statusCode: StatusCodes.Status400BadRequest);
                toDate = td;
            }

            var repeatedSearches = await usageLogger.GetRepeatedSearchesAsync(fromDate, toDate);
            var sequences = await usageLogger.GetToolSequencesAsync(fromDate, toDate);

            return Results.Ok(new
            {
                repeatedSearches = repeatedSearches.Select(r => new
                {
                    r.SessionId,
                    r.Query,
                    r.RepoCount
                }),
                toolSequences = sequences.Select(s => new
                {
                    s.FromTool,
                    s.ToTool,
                    s.Count
                })
            });
        })
        .WithSummary("Detected usage patterns — repeated queries, tool sequences");
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
