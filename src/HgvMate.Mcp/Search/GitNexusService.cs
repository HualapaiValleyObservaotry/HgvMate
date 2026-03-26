using HgvMate.Mcp.Configuration;
using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp.Search;

public record SymbolInfo(string Name, string Kind, string FilePath, int Line, IReadOnlyList<string> Callers, IReadOnlyList<string> Callees);
public record ReferenceInfo(string Symbol, string FilePath, int Line, string Context);

public class GitNexusService
{
    private readonly HgvMateOptions _hgvMateOptions;
    private readonly RepoSyncOptions _syncOptions;
    private readonly ILogger<GitNexusService> _logger;

    public GitNexusService(
        HgvMateOptions hgvMateOptions,
        RepoSyncOptions syncOptions,
        ILogger<GitNexusService> logger)
    {
        _hgvMateOptions = hgvMateOptions;
        _syncOptions = syncOptions;
        _logger = logger;
    }

    private string GetRepoPath(string repoName)
        => Path.Combine(_syncOptions.ResolveCloneRoot(_hgvMateOptions.DataPath), repoName);

    public async Task AnalyzeAsync(string repoName, CancellationToken cancellationToken = default)
    {
        var repoPath = GetRepoPath(repoName);
        _logger.LogInformation("Running GitNexus analysis on '{Repo}'...", repoName);
        await RunGitNexusCommandAsync("analyze", repoPath, cancellationToken);
    }

    public async Task<string> FindSymbolAsync(string symbolName, string? repoName = null, CancellationToken cancellationToken = default)
    {
        var repos = repoName != null ? [repoName] : await GetAvailableReposAsync();
        var results = new List<string>();

        foreach (var repo in repos)
        {
            var repoPath = GetRepoPath(repo);
            if (!IsGitNexusIndexed(repoPath))
            {
                results.Add($"[{repo}] Not indexed. Run hgvmate_reindex first.");
                continue;
            }

            var output = await RunGitNexusCommandAsync(
                $"query symbol \"{EscapeArg(symbolName)}\"",
                repoPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(output))
                results.Add($"[{repo}]\n{output}");
        }

        return results.Any() ? string.Join("\n\n", results) : $"Symbol '{symbolName}' not found.";
    }

    public async Task<string> GetReferencesAsync(string symbolName, string? repoName = null, CancellationToken cancellationToken = default)
    {
        var repos = repoName != null ? [repoName] : await GetAvailableReposAsync();
        var results = new List<string>();

        foreach (var repo in repos)
        {
            var repoPath = GetRepoPath(repo);
            if (!IsGitNexusIndexed(repoPath))
            {
                results.Add($"[{repo}] Not indexed. Run hgvmate_reindex first.");
                continue;
            }

            var output = await RunGitNexusCommandAsync(
                $"query references \"{EscapeArg(symbolName)}\"",
                repoPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(output))
                results.Add($"[{repo}]\n{output}");
        }

        return results.Any() ? string.Join("\n\n", results) : $"No references found for '{symbolName}'.";
    }

    public async Task<string> GetCallChainAsync(string symbolName, string? repoName = null, CancellationToken cancellationToken = default)
    {
        var repos = repoName != null ? [repoName] : await GetAvailableReposAsync();
        var results = new List<string>();

        foreach (var repo in repos)
        {
            var repoPath = GetRepoPath(repo);
            if (!IsGitNexusIndexed(repoPath))
            {
                results.Add($"[{repo}] Not indexed. Run hgvmate_reindex first.");
                continue;
            }

            var output = await RunGitNexusCommandAsync(
                $"query callchain \"{EscapeArg(symbolName)}\"",
                repoPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(output))
                results.Add($"[{repo}]\n{output}");
        }

        return results.Any() ? string.Join("\n\n", results) : $"No call chain found for '{symbolName}'.";
    }

    public async Task<string> GetImpactAsync(string symbolName, string? repoName = null, CancellationToken cancellationToken = default)
    {
        var repos = repoName != null ? [repoName] : await GetAvailableReposAsync();
        var results = new List<string>();

        foreach (var repo in repos)
        {
            var repoPath = GetRepoPath(repo);
            if (!IsGitNexusIndexed(repoPath))
            {
                results.Add($"[{repo}] Not indexed. Run hgvmate_reindex first.");
                continue;
            }

            var output = await RunGitNexusCommandAsync(
                $"query impact \"{EscapeArg(symbolName)}\"",
                repoPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(output))
                results.Add($"[{repo}]\n{output}");
        }

        return results.Any() ? string.Join("\n\n", results) : $"No impact found for '{symbolName}'.";
    }

    private bool IsGitNexusIndexed(string repoPath)
        => Directory.Exists(Path.Combine(repoPath, ".gitnexus"));

    private async Task<string[]> GetAvailableReposAsync()
    {
        var reposPath = _syncOptions.ResolveCloneRoot(_hgvMateOptions.DataPath);
        if (!Directory.Exists(reposPath))
            return [];
        return Directory.GetDirectories(reposPath)
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .ToArray();
    }

    private async Task<string> RunGitNexusCommandAsync(
        string args,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "gitnexus",
                Arguments = args,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
                _logger.LogWarning("gitnexus {Args} failed (code {Code}): {Error}", args, process.ExitCode, error);

            return output;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run gitnexus {Args}.", args);
            return $"GitNexus error: {ex.Message}";
        }
    }

    private static string EscapeArg(string value) => value.Replace("\"", "\\\"");
}
