using System.Diagnostics;
using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Search;
using HVO.Enterprise.Telemetry.Abstractions;
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
    private readonly StartupState _startupState;
    private readonly ITelemetryService? _telemetry;
    private readonly ILogger<RepoSyncService> _logger;

    // Limit concurrent single-repo syncs triggered externally (REST API, MCP tools)
    private readonly SemaphoreSlim _syncSemaphore = new(3, 3);

    // Pipeline semaphores: one ONNX job at a time, one GitNexus job at a time
    private readonly SemaphoreSlim _onnxSemaphore = new(1, 1);
    private readonly SemaphoreSlim _nexusSemaphore = new(1, 1);

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    /// <summary>Describes what indexing a repo needs after clone/pull.</summary>
    internal enum IndexAction { None, Full, Incremental, SkipVectors }

    /// <summary>Work item produced by the clone phase, consumed by ONNX and GitNexus workers.</summary>
    internal record SyncWorkItem(
        RepoRecord Repo,
        string NewSha,
        IndexAction Action,
        Stopwatch Timer,
        IReadOnlyList<string>? ChangedFiles = null);

    public RepoSyncService(
        IRepoRegistry registry,
        IGitCredentialProvider credentialProvider,
        HgvMateOptions hgvMateOptions,
        RepoSyncOptions syncOptions,
        IndexingService indexingService,
        GitNexusService gitNexusService,
        StartupState startupState,
        ILogger<RepoSyncService> logger,
        ITelemetryService? telemetry = null)
    {
        _registry = registry;
        _credentialProvider = credentialProvider;
        _hgvMateOptions = hgvMateOptions;
        _syncOptions = syncOptions;
        _indexingService = indexingService;
        _gitNexusService = gitNexusService;
        _startupState = startupState;
        _telemetry = telemetry;
        _logger = logger;
    }

    public string GetClonePath(string repoName)
        => Path.Combine(_syncOptions.ResolveCloneRoot(_hgvMateOptions.DataPath), repoName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield immediately so the host can finish starting (Kestrel begins accepting requests)
        await Task.Yield();

        // Wait for data stores to be initialized by WarmupService
        _logger.LogInformation("RepoSyncService waiting for warmup to complete...");
        while (!_startupState.IsReady && !stoppingToken.IsCancellationRequested)
            await Task.Delay(500, stoppingToken);

        _logger.LogInformation("RepoSyncService starting...");
        await RunSyncSafelyAsync(stoppingToken);

        if (_syncOptions.PollIntervalMinutes <= 0)
        {
            _logger.LogInformation("Polling disabled (PollIntervalMinutes=0). Use hgvmate_reindex to sync manually.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_syncOptions.PollIntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunSyncSafelyAsync(stoppingToken);
        }
    }

    private async Task RunSyncSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SyncAllAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Allow graceful shutdown
        }
        catch (Exception ex)
        {
            _telemetry?.TrackException(ex);
            _logger.LogError(ex, "SyncAll cycle failed. Will retry on next poll.");
        }
    }

    public virtual async Task SyncAllAsync(CancellationToken cancellationToken = default)
    {
        using var activity = HgvMateDiagnostics.ActivitySource.StartActivity("SyncAll");
        var repos = await _registry.GetAllAsync();
        var enabledRepos = repos.Where(r => r.Enabled).ToList();
        activity?.SetTag("hgvmate.repo.count", enabledRepos.Count);
        HgvMateDiagnostics.SetActiveRepoCount(enabledRepos.Count);

        // Pipeline: clone sequentially, ONNX + GitNexus run in parallel queues
        var postCloneTasks = new List<Task>();

        foreach (var repo in enabledRepos)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var workItem = await CloneAndPrepareAsync(repo, cancellationToken);
            if (workItem == null) continue; // up-to-date or failed

            // Fire off ONNX + GitNexus (each queued behind their semaphore)
            // Don't await — start cloning the next repo immediately
            var task = RunPostCloneAsync(workItem, cancellationToken);
            postCloneTasks.Add(task);
        }

        // Wait for all ONNX + GitNexus work to finish
        if (postCloneTasks.Count > 0)
            await Task.WhenAll(postCloneTasks);
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

    /// <summary>Single-repo sync: clone + parallel ONNX/GitNexus (no pipeline queuing).</summary>
    private async Task SyncRepoInternalAsync(RepoRecord repo, CancellationToken cancellationToken)
    {
        var workItem = await CloneAndPrepareAsync(repo, cancellationToken);
        if (workItem == null) return; // up-to-date or failed

        // Run ONNX and GitNexus in parallel for single-repo syncs
        await RunIndexingInParallelAsync(workItem, cancellationToken);
    }

    /// <summary>
    /// Clone/pull phase: fetch the repo, determine SHA, decide what indexing is needed.
    /// Returns null if the repo is up-to-date, or on clone failure (error is recorded).
    /// </summary>
    internal virtual async Task<SyncWorkItem?> CloneAndPrepareAsync(RepoRecord repo, CancellationToken cancellationToken)
    {
        using var activity = HgvMateDiagnostics.ActivitySource.StartActivity("SyncRepo");
        activity?.SetTag("hgvmate.repo.name", repo.Name);
        activity?.SetTag("hgvmate.repo.source", repo.Source);

        var clonePath = GetClonePath(repo.Name);
        _logger.LogInformation("Syncing repo '{Name}' to '{Path}'...", repo.Name, clonePath);
        await _registry.UpdateSyncStateAsync(repo.Name, SyncStates.Syncing);
        var sw = Stopwatch.StartNew();

        try
        {
            var oldSha = repo.LastSha;
            bool isFirstSync = !Directory.Exists(Path.Combine(clonePath, ".git"));
            activity?.SetTag("hgvmate.repo.is_first_sync", isFirstSync);

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
                await _registry.UpdateLastSyncedAsync(repo.Name, DateTime.UtcNow);
                return new SyncWorkItem(repo, "", IndexAction.Full, sw);
            }

            await _registry.UpdateLastSyncedAsync(repo.Name, DateTime.UtcNow);
            _logger.LogInformation("Repo '{Name}' synced successfully (SHA: {Sha}).", repo.Name, newSha);

            if (isFirstSync || string.IsNullOrEmpty(oldSha))
            {
                if (isFirstSync && newSha == oldSha && _indexingService.HasVectorsForRepo(repo.Name))
                {
                    _logger.LogInformation(
                        "Repo '{Name}' re-cloned but unchanged (SHA: {Sha}). Skipping vector re-index.",
                        repo.Name, newSha);
                    return new SyncWorkItem(repo, newSha, IndexAction.SkipVectors, sw);
                }
                return new SyncWorkItem(repo, newSha, IndexAction.Full, sw);
            }
            else if (oldSha != newSha)
            {
                var changedFiles = await GetChangedFilesAsync(clonePath, oldSha, newSha, cancellationToken);
                if (changedFiles.Count > 0)
                {
                    _logger.LogInformation("{Count} changed file(s) detected in '{Name}'. Re-indexing incrementally.", changedFiles.Count, repo.Name);
                    return new SyncWorkItem(repo, newSha, IndexAction.Incremental, sw, changedFiles);
                }
                else
                {
                    _logger.LogInformation("Could not determine changed files for '{Name}'; falling back to full re-index.", repo.Name);
                    return new SyncWorkItem(repo, newSha, IndexAction.Full, sw);
                }
            }
            else
            {
                _logger.LogInformation("Repo '{Name}' is up-to-date (SHA: {Sha}). Skipping re-index.", repo.Name, newSha);
                await _registry.ClearSyncErrorAsync(repo.Name);
                await _registry.UpdateSyncStateAsync(repo.Name, SyncStates.Synced);
                HgvMateDiagnostics.RepoSyncTotal.Add(1, new KeyValuePair<string, object?>("repo", repo.Name), new KeyValuePair<string, object?>("status", "success"));
                HgvMateDiagnostics.RepoSyncDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("repo", repo.Name));
                return null; // up-to-date
            }
        }
        catch (Exception ex)
        {
            _telemetry?.TrackException(ex);
            _telemetry?.TrackEvent("hgvmate.repo.sync_failed");
            HgvMateDiagnostics.RepoSyncErrors.Add(1, new KeyValuePair<string, object?>("repo", repo.Name));
            HgvMateDiagnostics.RepoSyncTotal.Add(1, new KeyValuePair<string, object?>("repo", repo.Name), new KeyValuePair<string, object?>("status", "error"));
            _logger.LogError(ex, "Failed to sync repo '{Name}'.", repo.Name);
            await _registry.UpdateSyncErrorAsync(repo.Name, ex.Message);
            HgvMateDiagnostics.RepoSyncDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("repo", repo.Name));
            return null;
        }
    }

    /// <summary>Pipeline post-clone: queues ONNX and GitNexus behind their respective semaphores.</summary>
    private async Task RunPostCloneAsync(SyncWorkItem item, CancellationToken cancellationToken)
    {
        var onnxTask = Task.Run(async () =>
        {
            await _onnxSemaphore.WaitAsync(cancellationToken);
            try { await RunOnnxIndexingAsync(item, cancellationToken); }
            finally { _onnxSemaphore.Release(); }
        }, cancellationToken);

        var nexusTask = Task.Run(async () =>
        {
            await _nexusSemaphore.WaitAsync(cancellationToken);
            try { await RunGitNexusAnalysisAsync(item.Repo.Name, cancellationToken); }
            finally { _nexusSemaphore.Release(); }
        }, cancellationToken);

        try
        {
            await Task.WhenAll(onnxTask, nexusTask);
            await FinalizeRepoAsync(item);
        }
        catch (Exception ex)
        {
            _telemetry?.TrackException(ex);
            _logger.LogError(ex, "Post-clone indexing failed for repo '{Name}'.", item.Repo.Name);
            await _registry.UpdateSyncErrorAsync(item.Repo.Name, ex.Message);
        }
    }

    /// <summary>Direct parallel execution for single-repo syncs (no semaphore queuing).</summary>
    private async Task RunIndexingInParallelAsync(SyncWorkItem item, CancellationToken cancellationToken)
    {
        try
        {
            var onnxTask = RunOnnxIndexingAsync(item, cancellationToken);
            var nexusTask = RunGitNexusAnalysisAsync(item.Repo.Name, cancellationToken);
            await Task.WhenAll(onnxTask, nexusTask);
            await FinalizeRepoAsync(item);
        }
        catch (Exception ex)
        {
            _telemetry?.TrackException(ex);
            _logger.LogError(ex, "Indexing failed for repo '{Name}'.", item.Repo.Name);
            await _registry.UpdateSyncErrorAsync(item.Repo.Name, ex.Message);
        }
    }

    private async Task RunOnnxIndexingAsync(SyncWorkItem item, CancellationToken cancellationToken)
    {
        switch (item.Action)
        {
            case IndexAction.Full:
                await _indexingService.IndexRepoAsync(item.Repo.Name, cancellationToken);
                break;
            case IndexAction.Incremental:
                foreach (var file in item.ChangedFiles!)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    await _indexingService.IndexFileAsync(item.Repo.Name, file, cancellationToken);
                }
                await _indexingService.SaveVectorStoreAsync();
                break;
            case IndexAction.SkipVectors:
            case IndexAction.None:
                break;
        }
    }

    private async Task FinalizeRepoAsync(SyncWorkItem item)
    {
        if (!string.IsNullOrEmpty(item.NewSha))
            await _registry.UpdateLastShaAsync(item.Repo.Name, item.NewSha);
        await _registry.ClearSyncErrorAsync(item.Repo.Name);
        await _registry.UpdateSyncStateAsync(item.Repo.Name, SyncStates.Synced);
        _telemetry?.TrackEvent("hgvmate.repo.sync_completed");
        HgvMateDiagnostics.RepoSyncTotal.Add(1, new KeyValuePair<string, object?>("repo", item.Repo.Name), new KeyValuePair<string, object?>("status", "success"));
        HgvMateDiagnostics.RepoSyncDuration.Record(item.Timer.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("repo", item.Repo.Name));
        _telemetry?.RecordMetric("hgvmate.repo.sync_duration_ms", item.Timer.Elapsed.TotalMilliseconds);
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
