using HgvMate.Mcp.Repos;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace HgvMate.Mcp.Tools;

[McpServerToolType]
public class AdminTools
{
    private readonly IRepoRegistry _registry;
    private readonly RepoSyncService _syncService;

    public AdminTools(IRepoRegistry registry, RepoSyncService syncService)
    {
        _registry = registry;
        _syncService = syncService;
    }

    [McpServerTool(Name = "hgvmate_add_repository")]
    [Description("Add a repository to be indexed. Source: 'github' or 'azuredevops'.")]
    public async Task<string> AddRepository(
        [Description("Unique name for the repository")] string name,
        [Description("Clone URL of the repository")] string url,
        [Description("Branch to clone (default: main)")] string branch = "main",
        [Description("Source type: 'github' or 'azuredevops' (default: github)")] string source = "github")
    {
        HgvMateDiagnostics.RecordToolCall("add_repository");
        if (string.IsNullOrWhiteSpace(name))
            return "Error: name is required.";
        if (string.IsNullOrWhiteSpace(url))
            return "Error: url is required.";

        var validSources = new[] { "github", "azuredevops" };
        if (!validSources.Contains(source.ToLowerInvariant()))
            return $"Error: source must be one of: {string.Join(", ", validSources)}.";

        try
        {
            var existing = await _registry.GetByNameAsync(name);
            if (existing != null)
                return $"Error: Repository '{name}' already exists.";

            var existingUrl = await _registry.GetByUrlAsync(url);
            if (existingUrl != null)
                return $"Error: A repository with the same URL is already registered as '{existingUrl.Name}' (branch: {existingUrl.Branch}). " +
                       "Adding the same repo with a different branch would create mostly duplicate search results.";

            var repo = await _registry.AddAsync(name, url, branch, source.ToLowerInvariant(), addedBy: "mcp-tool");
            _ = Task.Run(() => _syncService.SyncRepoAsync(repo));
            return $"Repository '{name}' added and sync initiated. Use hgvmate_index_status to track progress.";
        }
        catch (Exception ex)
        {
            return $"Error adding repository: {ex.Message}";
        }
    }

    [McpServerTool(Name = "hgvmate_remove_repository")]
    [Description("Remove a repository and delete its cloned data.")]
    public async Task<string> RemoveRepository(
        [Description("Name of the repository to remove")] string name)
    {
        HgvMateDiagnostics.RecordToolCall("remove_repository");
        if (string.IsNullOrWhiteSpace(name))
            return "Error: name is required.";

        try
        {
            var repo = await _registry.GetByNameAsync(name);
            if (repo == null)
                return $"Error: Repository '{name}' not found.";

            await _syncService.DeleteRepoCloneAsync(name);
            await _registry.RemoveAsync(name);
            return $"Repository '{name}' removed successfully.";
        }
        catch (Exception ex)
        {
            return $"Error removing repository: {ex.Message}";
        }
    }

    [McpServerTool(Name = "hgvmate_list_repositories")]
    [Description("List all registered repositories with their sync status.")]
    public async Task<string> ListRepositories()
    {
        HgvMateDiagnostics.RecordToolCall("list_repositories");
        try
        {
            var repos = await _registry.GetAllAsync();
            if (!repos.Any())
                return "No repositories registered. Use hgvmate_add_repository to add one.";

            var lines = repos.Select(r =>
                $"- {r.Name} | {r.Url} | branch: {r.Branch} | source: {r.Source} | enabled: {r.Enabled} | last_sha: {r.LastSha ?? "none"} | last_synced: {r.LastSynced ?? "never"}");
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error listing repositories: {ex.Message}";
        }
    }

    [McpServerTool(Name = "hgvmate_reindex")]
    [Description("Trigger an immediate sync and re-index for one or all repositories.")]
    public async Task<string> Reindex(
        [Description("Repository name to reindex, or omit for all repositories")] string? repository = null)
    {
        HgvMateDiagnostics.RecordToolCall("reindex");
        try
        {
            if (!string.IsNullOrWhiteSpace(repository))
            {
                var repo = await _registry.GetByNameAsync(repository);
                if (repo == null)
                    return $"Error: Repository '{repository}' not found.";
                _ = Task.Run(() => _syncService.SyncRepoAsync(repo));
                return $"Reindex triggered for '{repository}'.";
            }
            else
            {
                _ = Task.Run(() => _syncService.SyncAllAsync());
                return "Reindex triggered for all repositories.";
            }
        }
        catch (Exception ex)
        {
            return $"Error triggering reindex: {ex.Message}";
        }
    }

    [McpServerTool(Name = "hgvmate_index_status")]
    [Description("Get per-repository index status showing clone state, last SHA, and sync status.")]
    public async Task<string> IndexStatus(
        [Description("Repository name to check, or omit for all repositories")] string? repository = null)
    {
        HgvMateDiagnostics.RecordToolCall("index_status");
        try
        {
            IReadOnlyList<RepoRecord> repos;
            if (!string.IsNullOrWhiteSpace(repository))
            {
                var repo = await _registry.GetByNameAsync(repository);
                if (repo == null)
                    return $"Error: Repository '{repository}' not found.";
                repos = [repo];
            }
            else
            {
                repos = await _registry.GetAllAsync();
            }

            if (!repos.Any())
                return "No repositories registered.";

            var lines = repos.Select(r =>
                $"- {r.Name}: enabled={r.Enabled}, sha={r.LastSha ?? "none"}, synced={r.LastSynced ?? "never"}, source={r.Source}, state={r.SyncState}{(r.LastError != null ? $", error={r.LastError}" : "")}");
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error getting index status: {ex.Message}";
        }
    }
}
