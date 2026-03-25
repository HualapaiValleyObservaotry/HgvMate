using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Search;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp.Repos;

public class RepoSyncService : BackgroundService
{
    private readonly IRepoRegistry _registry;
    private readonly IGitCredentialProvider _credentialProvider;
    private readonly HgvMateOptions _hgvMateOptions;
    private readonly RepoSyncOptions _syncOptions;
    private readonly IndexingService _indexingService;
    private readonly GitNexusService _gitNexusService;
    private readonly ILogger<RepoSyncService> _logger;

    public RepoSyncService(
        IRepoRegistry registry,
        IGitCredentialProvider credentialProvider,
        HgvMateOptions hgvMateOptions,
        RepoSyncOptions syncOptions,
        IndexingService indexingService,
        GitNexusService gitNexusService,
        ILogger<RepoSyncService> logger)
    {
        _registry = registry;
        _credentialProvider = credentialProvider;
        _hgvMateOptions = hgvMateOptions;
        _syncOptions = syncOptions;
        _indexingService = indexingService;
        _gitNexusService = gitNexusService;
        _logger = logger;
    }

    public string GetClonePath(string repoName)
        => Path.Combine(_hgvMateOptions.DataPath, _syncOptions.ClonePath, repoName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RepoSyncService starting...");
        await SyncAllAsync(stoppingToken);

        if (_syncOptions.PollIntervalMinutes <= 0)
        {
            _logger.LogInformation("Polling disabled (PollIntervalMinutes=0). Use hgvmate_reindex to sync manually.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_syncOptions.PollIntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SyncAllAsync(stoppingToken);
        }
    }

    public virtual async Task SyncAllAsync(CancellationToken cancellationToken = default)
    {
        var repos = await _registry.GetAllAsync();
        foreach (var repo in repos.Where(r => r.Enabled))
        {
            if (cancellationToken.IsCancellationRequested) break;
            await SyncRepoAsync(repo, cancellationToken);
        }
    }

    public virtual async Task SyncRepoAsync(RepoRecord repo, CancellationToken cancellationToken = default)
    {
        var clonePath = GetClonePath(repo.Name);
        _logger.LogInformation("Syncing repo '{Name}' to '{Path}'...", repo.Name, clonePath);

        try
        {
            if (!Directory.Exists(Path.Combine(clonePath, ".git")))
            {
                await CloneRepoAsync(repo, clonePath, cancellationToken);
            }
            else
            {
                await PullRepoAsync(repo, clonePath, cancellationToken);
            }

            var sha = await GetCurrentShaAsync(clonePath, cancellationToken);
            if (!string.IsNullOrEmpty(sha))
                await _registry.UpdateLastShaAsync(repo.Name, sha);

            await _registry.UpdateLastSyncedAsync(repo.Name, DateTime.UtcNow);
            _logger.LogInformation("Repo '{Name}' synced successfully (SHA: {Sha}).", repo.Name, sha ?? "unknown");

            await _indexingService.IndexRepoAsync(repo.Name, cancellationToken);

            try
            {
                await _gitNexusService.AnalyzeAsync(repo.Name, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GitNexus analysis failed for '{Name}'; continuing.", repo.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync repo '{Name}'.", repo.Name);
        }
    }

    private async Task CloneRepoAsync(RepoRecord repo, string clonePath, CancellationToken cancellationToken)
    {
        var authUrl = _credentialProvider.BuildAuthenticatedUrl(repo.Url, repo.Source);
        Directory.CreateDirectory(clonePath);
        await RunGitAsync(
            ["clone", "--depth", "1", "--single-branch", "--branch", repo.Branch, authUrl, "."],
            clonePath,
            cancellationToken);
    }

    private async Task PullRepoAsync(RepoRecord repo, string clonePath, CancellationToken cancellationToken)
    {
        await RunGitAsync(["fetch", "--depth", "1", "origin", repo.Branch], clonePath, cancellationToken);
        await RunGitAsync(["reset", "--hard", $"origin/{repo.Branch}"], clonePath, cancellationToken);
    }

    private async Task<string?> GetCurrentShaAsync(string clonePath, CancellationToken cancellationToken)
    {
        var (output, _) = await RunGitAsync(["rev-parse", "HEAD"], clonePath, cancellationToken);
        return output.Trim();
    }

    private async Task<(string output, int exitCode)> RunGitAsync(
        string[] args,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
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
        {
            _logger.LogWarning("git {Args} exited with code {Code}: {Error}",
                string.Join(" ", args), process.ExitCode, error);
        }

        return (output, process.ExitCode);
    }

    public async Task DeleteRepoCloneAsync(string repoName)
    {
        var clonePath = GetClonePath(repoName);
        if (Directory.Exists(clonePath))
        {
            _logger.LogInformation("Deleting clone directory '{Path}'.", clonePath);
            await Task.Run(() => Directory.Delete(clonePath, recursive: true));
        }
    }
}
