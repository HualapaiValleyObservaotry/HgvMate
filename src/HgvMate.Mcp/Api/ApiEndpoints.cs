using HgvMate.Mcp.Repos;
using HgvMate.Mcp.Search;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HgvMate.Mcp.Api;

public static class ApiEndpoints
{
    public static WebApplication MapRestApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        MapRepositoryEndpoints(api);
        MapSearchEndpoints(api);
        MapStructuralEndpoints(api);

        return app;
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

        repos.MapPost("/", async (AddRepoRequest request, IRepoRegistry registry, RepoSyncService syncService) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "name is required." });
            if (string.IsNullOrWhiteSpace(request.Url))
                return Results.BadRequest(new { error = "url is required." });

            var validSources = new[] { "github", "azuredevops" };
            var source = request.Source ?? "github";
            if (!validSources.Contains(source, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"source must be one of: {string.Join(", ", validSources)}." });

            var existing = await registry.GetByNameAsync(request.Name);
            if (existing != null)
                return Results.Conflict(new { error = $"Repository '{request.Name}' already exists." });

            var repo = await registry.AddAsync(request.Name, request.Url, request.Branch ?? "main", source.ToLowerInvariant(), addedBy: "rest-api");
            _ = Task.Run(() => syncService.SyncRepoAsync(repo));
            return Results.Created($"/api/repositories/{repo.Name}", repo);
        })
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
        .WithSummary("Remove a repository and delete its cloned data");

        repos.MapPost("/{name}/reindex", async (string name, IRepoRegistry registry, RepoSyncService syncService) =>
        {
            var repo = await registry.GetByNameAsync(name);
            if (repo == null)
                return Results.NotFound(new { error = $"Repository '{name}' not found." });

            _ = Task.Run(() => syncService.SyncRepoAsync(repo));
            return Results.Accepted(value: new { message = $"Reindex triggered for '{name}'." });
        })
        .WithSummary("Trigger reindex for a specific repository");

        repos.MapPost("/reindex", async (IRepoRegistry registry, RepoSyncService syncService) =>
        {
            _ = Task.Run(() => syncService.SyncAllAsync());
            return TypedResults.Ok(new { message = "Reindex triggered for all repositories." });
        })
        .WithSummary("Trigger reindex for all repositories");

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
                repo.Source
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
                r.Source
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
}

public record AddRepoRequest(
    string Name,
    string Url,
    string? Branch = "main",
    string? Source = "github"
);
