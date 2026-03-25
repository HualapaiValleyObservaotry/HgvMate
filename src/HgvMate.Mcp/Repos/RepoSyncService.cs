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

    // Limit concurrent syncs to prevent OOM from unbounded parallel git clones + ONNX indexing
    private readonly SemaphoreSlim _syncSemaphore = new(3, 3);

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

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
        await _syncSemaphore.WaitAsync(cancellationToken);
        try
        {
            await SyncRepoInternalAsync(repo, cancellationToken);
        }
        finally
        {
            _syncSemaphore.Release();
        }
    }

    private async Task SyncRepoInternalAsync(RepoRecord repo, CancellationToken cancellationToken)
    {
        var clonePath = GetClonePath(repo.Name);
        _logger.LogInformation("Syncing repo '{Name}' to '{Path}'...", repo.Name, clonePath);
        await _registry.UpdateSyncStateAsync(repo.Name, SyncStates.Syncing);

        try
        {
            var oldSha = repo.LastSha;
            bool isFirstSync = !Directory.Exists(Path.Combine(clonePath, ".git"));

            if (isFirstSync)
            {
                await CloneWithRetryAsync(repo, clonePath, cancellationToken);
            }
            else
            {
                await PullWithRetryAsync(repo, clonePath, cancellationToken);
            }

            var newSha = await GetCurrentShaAsync(clonePath, cancellationToken);

            if (string.IsNullOrEmpty(newSha))
            {
                _logger.LogWarning("Could not determine HEAD SHA for repo '{Name}'. Falling back to full re-index.", repo.Name);
                await _indexingService.IndexRepoAsync(repo.Name, cancellationToken);
                await RunGitNexusAnalysisAsync(repo.Name, cancellationToken);
                await _registry.UpdateLastSyncedAsync(repo.Name, DateTime.UtcNow);
                await _registry.ClearSyncErrorAsync(repo.Name);
                return;
            }

            await _registry.UpdateLastSyncedAsync(repo.Name, DateTime.UtcNow);
            _logger.LogInformation("Repo '{Name}' synced successfully (SHA: {Sha}).", repo.Name, newSha);

            if (isFirstSync || string.IsNullOrEmpty(oldSha))
            {
                // First sync: full index
                await _indexingService.IndexRepoAsync(repo.Name, cancellationToken);
                await RunGitNexusAnalysisAsync(repo.Name, cancellationToken);
                await _registry.UpdateLastShaAsync(repo.Name, newSha);
            }
            else if (oldSha != newSha)
            {
                // Changes detected: try incremental re-index
                var changedFiles = await GetChangedFilesAsync(clonePath, oldSha, newSha, cancellationToken);
                if (changedFiles.Count > 0)
                {
                    _logger.LogInformation("{Count} changed file(s) detected in '{Name}'. Re-indexing incrementally.", changedFiles.Count, repo.Name);
                    foreach (var file in changedFiles)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        await _indexingService.IndexFileAsync(repo.Name, file, cancellationToken);
                    }
                    await RunGitNexusAnalysisAsync(repo.Name, cancellationToken);
                }
                else
                {
                    // Diff failed or returned nothing (e.g. shallow history); fall back to full re-index
                    _logger.LogInformation("Could not determine changed files for '{Name}'; falling back to full re-index.", repo.Name);
                    await _indexingService.IndexRepoAsync(repo.Name, cancellationToken);
                    await RunGitNexusAnalysisAsync(repo.Name, cancellationToken);
                }
                await _registry.UpdateLastShaAsync(repo.Name, newSha);
            }
            else
            {
                _logger.LogInformation("Repo '{Name}' is up-to-date (SHA: {Sha}). Skipping re-index.", repo.Name, newSha);
            }

            await _registry.ClearSyncErrorAsync(repo.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync repo '{Name}'.", repo.Name);
            await _registry.UpdateSyncErrorAsync(repo.Name, ex.Message);
        }
    }

    internal async Task<IReadOnlyList<string>> GetChangedFilesAsync(
        string clonePath, string oldSha, string newSha, CancellationToken cancellationToken = default)
    {
        var (output, exitCode) = await RunGitAsync(
            ["diff", "--name-only", $"{oldSha}..{newSha}"],
            clonePath,
            cancellationToken);

        if (exitCode != 0)
            return [];

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToList();
    }

    private async Task RunGitNexusAnalysisAsync(string repoName, CancellationToken cancellationToken)
    {
        try
        {
            await _gitNexusService.AnalyzeAsync(repoName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitNexus analysis failed for '{Name}'; continuing.", repoName);
        }
    }

    private async Task CloneWithRetryAsync(RepoRecord repo, string clonePath, CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(
            () => CloneRepoAsync(repo, clonePath, cancellationToken),
            repo.Name,
            "clone",
            cancellationToken);
    }

    private async Task PullWithRetryAsync(RepoRecord repo, string clonePath, CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(
            () => PullRepoAsync(repo, clonePath, cancellationToken),
            repo.Name,
            "pull",
            cancellationToken);
    }

    private async Task ExecuteWithRetryAsync(Func<Task> operation, string repoName, string operationName, CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (int attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                await operation();
                return;
            }
            catch (Exception ex) when (attempt < RetryDelays.Length && IsTransientError(ex))
            {
                lastException = ex;
                var delay = RetryDelays[attempt];
                _logger.LogWarning(ex,
                    "Transient error during {Operation} for repo '{Name}' (attempt {Attempt}/{Max}). Retrying in {Delay}s...",
                    operationName, repoName, attempt + 1, RetryDelays.Length + 1, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        if (lastException != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(lastException).Throw();

        throw new InvalidOperationException($"{operationName} failed for repo '{repoName}' after all retry attempts.");
    }

    /// <summary>
    /// Returns true for errors that are worth retrying (network blips, rate limits, auth timeouts).
    /// Returns false for permanent failures (repo not found, access denied).
    /// </summary>
    internal static bool IsTransientError(Exception ex)
    {
        // Cancellations are not transient — propagate immediately
        if (ex is OperationCanceledException)
            return false;

        var msg = ex.Message.ToLowerInvariant();

        // Permanent errors — do not retry
        if (msg.Contains("repository not found") ||
            msg.Contains("not found") && msg.Contains("remote") ||
            msg.Contains("access denied") ||
            msg.Contains("authentication failed") ||
            msg.Contains("permission denied") ||
            msg.Contains("does not exist"))
        {
            return false;
        }

        // Transient errors — retry
        if (msg.Contains("timed out") ||
            msg.Contains("timeout") ||
            msg.Contains("connection reset") ||
            msg.Contains("connection refused") ||
            msg.Contains("unable to connect") ||
            msg.Contains("could not resolve host") ||
            msg.Contains("rate limit") ||
            msg.Contains("429") ||
            msg.Contains("503") ||
            msg.Contains("temporary") ||
            ex is IOException ||
            ex is TimeoutException)
        {
            return true;
        }

        // Unknown errors — treat as transient to avoid silent permanent failures
        return true;
    }

    private async Task CloneRepoAsync(RepoRecord repo, string clonePath, CancellationToken cancellationToken)
    {
        EnsureSufficientDiskSpace(clonePath);
        var authUrl = _credentialProvider.BuildAuthenticatedUrl(repo.Url, repo.Source);
        Directory.CreateDirectory(clonePath);
        var (_, exitCode) = await RunGitAsync(
            ["clone", "--depth", "1", "--single-branch", "--branch", repo.Branch, authUrl, "."],
            clonePath,
            cancellationToken);

        if (exitCode != 0)
            throw new InvalidOperationException($"git clone exited with code {exitCode} for repo '{repo.Name}'.");
    }

    private async Task PullRepoAsync(RepoRecord repo, string clonePath, CancellationToken cancellationToken)
    {
        var (_, fetchCode) = await RunGitAsync(["fetch", "--depth", "1", "origin", repo.Branch], clonePath, cancellationToken);
        if (fetchCode != 0)
            throw new InvalidOperationException($"git fetch exited with code {fetchCode} for repo '{repo.Name}'.");

        var (_, resetCode) = await RunGitAsync(["reset", "--hard", $"origin/{repo.Branch}"], clonePath, cancellationToken);
        if (resetCode != 0)
            throw new InvalidOperationException($"git reset exited with code {resetCode} for repo '{repo.Name}'.");
    }

    private async Task<string?> GetCurrentShaAsync(string clonePath, CancellationToken cancellationToken)
    {
        var (output, _) = await RunGitAsync(["rev-parse", "HEAD"], clonePath, cancellationToken);
        return output.Trim();
    }

    protected virtual async Task<(string output, int exitCode)> RunGitAsync(
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

    internal void EnsureSufficientDiskSpace(string targetPath)
    {
        if (_syncOptions.MinFreeDiskSpaceMb <= 0) return;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(targetPath)) ?? targetPath;
            var driveInfo = new DriveInfo(root);
            var freeMb = driveInfo.AvailableFreeSpace / (1024 * 1024);

            if (freeMb < _syncOptions.MinFreeDiskSpaceMb)
            {
                throw new InvalidOperationException(
                    $"Insufficient disk space. Available: {freeMb} MB, required minimum: {_syncOptions.MinFreeDiskSpaceMb} MB. " +
                    $"Free up space or adjust RepoSync:MinFreeDiskSpaceMb in configuration.");
            }
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check disk space for '{Path}'. Proceeding with clone.", targetPath);
        }
    }
}
