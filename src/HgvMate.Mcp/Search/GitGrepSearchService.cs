using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Repos;
using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp.Search;

public record GrepResult(string RepoName, string FilePath, int LineNumber, string LineContent);

public class GitGrepSearchService
{
    private readonly IRepoRegistry _registry;
    private readonly SourceCodeReader _reader;
    private readonly SearchOptions _searchOptions;
    private readonly ILogger<GitGrepSearchService> _logger;

    public GitGrepSearchService(
        IRepoRegistry registry,
        SourceCodeReader reader,
        SearchOptions searchOptions,
        ILogger<GitGrepSearchService> logger)
    {
        _registry = registry;
        _reader = reader;
        _searchOptions = searchOptions;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GrepResult>> SearchAsync(
        string query,
        string? repositoryName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var repos = await _registry.GetAllAsync();
        var targetRepos = repos
            .Where(r => r.Enabled)
            .Where(r => repositoryName == null || r.Name.Equals(repositoryName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var results = new List<GrepResult>();
        foreach (var repo in targetRepos)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var repoResults = await SearchRepoAsync(repo.Name, query, cancellationToken);
            results.AddRange(repoResults);
        }

        return results.Take(_searchOptions.MaxResults).ToList();
    }

    private async Task<IReadOnlyList<GrepResult>> SearchRepoAsync(
        string repoName,
        string query,
        CancellationToken cancellationToken)
    {
        var repoRoot = _reader.GetRepoRoot(repoName);
        if (!Directory.Exists(Path.Combine(repoRoot, ".git")))
        {
            _logger.LogWarning("Repo '{Name}' not cloned yet, skipping grep.", repoName);
            return [];
        }

        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                // -n: line numbers, -I: skip binary, --no-color: plain output
                Arguments = $"grep -n -I --no-color -e \"{EscapeForShell(query)}\"",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            // Exit code 1 means no matches (not an error), exit code > 1 means error
            if (process.ExitCode > 1)
            {
                _logger.LogWarning("git grep exited with code {Code} for repo '{Name}'.", process.ExitCode, repoName);
                return [];
            }

            return ParseGrepOutput(repoName, output);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run git grep in repo '{Name}'.", repoName);
            return [];
        }
    }

    private IReadOnlyList<GrepResult> ParseGrepOutput(string repoName, string output)
    {
        var results = new List<GrepResult>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Format: filepath:linenum:content
            var parts = line.Split(':', 3);
            if (parts.Length >= 3
                && int.TryParse(parts[1], out var lineNum))
            {
                results.Add(new GrepResult(repoName, parts[0], lineNum, parts[2]));
            }
        }
        return results;
    }

    private static string EscapeForShell(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
