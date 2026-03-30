using System.Diagnostics;
using System.Threading.Channels;
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

    // Limit concurrent syncs to prevent OOM from unbounded parallel git clones + ONNX indexing
    private readonly SemaphoreSlim _syncSemaphore = new(3, 3);

    // Bounded channel for fire-and-forget GitNexus analysis (ONNX and GitNexus overlap without contention).
    // Capacity 256 covers large bulk syncs. DropWrite + TryWrite: when full, the incoming enqueue is
    // dropped (best-effort). GitNexus is idempotent, so a missed analysis is self-healing on the next
    // sync cycle. The 256-slot buffer is large enough that drops are rare in practice.
    private readonly Channel<string> _gitNexusQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });
    private readonly SemaphoreSlim _gitNexusSemaphore = new(3, 3);

    // Extensions that warrant GitNexus structural re-analysis (AST-parseable source files)
    private static readonly HashSet<string> StructuralExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".js", ".tsx", ".jsx", ".py", ".java", ".go", ".rs",
        ".cpp", ".c", ".h", ".hpp", ".rb", ".php", ".swift", ".kt", ".scala"
    };

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

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

        // Start the GitNexus background worker — runs concurrently with ONNX embedding
        var gitNexusWorker = RunGitNexusWorkerAsync(stoppingToken);

        try
        {
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
        finally
        {
            // Always signal the worker to drain and await it for a clean shutdown
            _gitNexusQueue.Writer.TryComplete();
            await gitNexusWorker;
        }
    }

    /// <summary>
    /// Background worker that drains the GitNexus analysis queue. Running separately from the
    /// sync loop lets ONNX embedding (CPU-bound) and GitNexus analysis (I/O-bound) overlap.
    /// Tracks all in-flight analysis tasks and awaits them before returning so shutdown is clean.
    /// </summary>
    private async Task RunGitNexusWorkerAsync(CancellationToken cancellationToken)
    {
        // HashSet instead of List so we can prune completed tasks on each iteration,
        // preventing unbounded growth across the service lifetime.
        var inFlightTasks = new HashSet<Task>();
        try
        {
            await foreach (var repoName in _gitNexusQueue.Reader.ReadAllAsync(cancellationToken))
            {
                await _gitNexusSemaphore.WaitAsync(cancellationToken);
                // Do not pass cancellationToken to Task.Run — a cancelled token would prevent the
                // delegate from running while still holding the semaphore permit (leak).
                // Instead, handle cancellation inside the delegate itself.
                var task = Task.Run(async () =>
                {
                    try
                    {
                        await _gitNexusService.AnalyzeAsync(repoName, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutting down — analysis abandoned gracefully
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Background GitNexus analysis failed for '{Repo}'; continuing.", repoName);
                    }
                    finally
                    {
                        _gitNexusSemaphore.Release();
                    }
                });
                inFlightTasks.Add(task);
                // Remove completed tasks to prevent the set growing unbounded
                inFlightTasks.RemoveWhere(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown signaled — fall through to await any already-started tasks
        }
        finally
        {
            // Await all in-flight analyses so they are not abandoned on shutdown
            if (inFlightTasks.Count > 0)
                await Task.WhenAll(inFlightTasks);
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
        => await SyncAllAsync(force: false, cancellationToken);

    public virtual async Task SyncAllAsync(bool force, CancellationToken cancellationToken = default)
    {
        using var activity = HgvMateDiagnostics.ActivitySource.StartActivity("SyncAll");
        var repos = await _registry.GetAllAsync();
        var enabledRepos = repos.Where(r => r.Enabled).ToList();
        activity?.SetTag("hgvmate.repo.count", enabledRepos.Count);
        activity?.SetTag("hgvmate.sync.force", force);
        HgvMateDiagnostics.SetActiveRepoCount(enabledRepos.Count);

        if (force)
            _logger.LogInformation("Force reindex requested for all {Count} enabled repositories.", enabledRepos.Count);

        bool isBulkSync = enabledRepos.Count > 1;

        // Collect deferred SHA updates — applied only after a successful vector store flush
        // to avoid registry/vector-store inconsistency if the process crashes mid-bulk.
        var deferredShas = isBulkSync ? new System.Collections.Concurrent.ConcurrentDictionary<string, string>() : null;

        bool parallelCompleted = false;
        try
        {
            // Process repos in parallel (up to 3 concurrent) — the semaphore inside SyncRepoAsync gates
            // individual resource use; Parallel.ForEachAsync provides the outer concurrency control.
            await Parallel.ForEachAsync(enabledRepos, new ParallelOptions
            {
                MaxDegreeOfParallelism = 3,
                CancellationToken = cancellationToken
            }, async (repo, ct) =>
            {
                await SyncRepoAsync(repo, force, isBulkSync, deferredShas, ct);
            });
            parallelCompleted = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Partial progress: flush any vectors already indexed so the work is not lost.
            // SHA updates are skipped — next sync cycle will re-detect changes.
            if (isBulkSync && deferredShas!.Count > 0)
            {
                _logger.LogInformation("Bulk sync canceled; flushing partial vectors for {Count} repo(s).", deferredShas.Count);
                try { await _indexingService.SaveVectorStoreAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Partial vector flush failed after cancellation."); }
            }
            throw;
        }
        finally
        {
            // On clean completion: flush vectors, then update all deferred SHAs in sequence.
            // SHA updates happen after a successful SaveVectorStoreAsync so the registry never
            // reports a repo as synced while its embeddings are absent from vectors.bin.
            // Only runs if repos actually used deferSave (deferredShas populated) to avoid
            // an unnecessary vectors.bin rewrite on poll cycles where everything is up-to-date.
            if (parallelCompleted && isBulkSync && deferredShas is { Count: > 0 })
            {
                _logger.LogInformation("Bulk sync complete. Flushing vector store for {Count} repo(s)...", deferredShas.Count);
                await _indexingService.SaveVectorStoreAsync();
                foreach (var (name, sha) in deferredShas)
                    await _registry.UpdateLastShaAsync(name, sha);
            }
        }
    }

    public virtual async Task SyncRepoAsync(RepoRecord repo, CancellationToken cancellationToken = default)
        => await SyncRepoAsync(repo, force: false, isBulkSync: false, deferredShas: null, cancellationToken);

    public virtual async Task SyncRepoAsync(RepoRecord repo, bool force, CancellationToken cancellationToken = default)
        => await SyncRepoAsync(repo, force, isBulkSync: false, deferredShas: null, cancellationToken);

    public virtual async Task SyncRepoAsync(RepoRecord repo, bool force, bool isBulkSync, CancellationToken cancellationToken = default)
        => await SyncRepoAsync(repo, force, isBulkSync, deferredShas: null, cancellationToken);

    internal virtual async Task SyncRepoAsync(
        RepoRecord repo, bool force, bool isBulkSync,
        System.Collections.Concurrent.ConcurrentDictionary<string, string>? deferredShas,
        CancellationToken cancellationToken = default)
    {
        await _syncSemaphore.WaitAsync(cancellationToken);
        try
        {
            await SyncRepoInternalAsync(repo, force, isBulkSync, deferredShas, cancellationToken);
        }
        finally
        {
            _syncSemaphore.Release();
        }
    }

    private async Task SyncRepoInternalAsync(
        RepoRecord repo, bool force, bool isBulkSync,
        System.Collections.Concurrent.ConcurrentDictionary<string, string>? deferredShas,
        CancellationToken cancellationToken)
    {
        using var activity = HgvMateDiagnostics.ActivitySource.StartActivity("SyncRepo");
        activity?.SetTag("hgvmate.repo.name", repo.Name);
        activity?.SetTag("hgvmate.repo.source", repo.Source);
        activity?.SetTag("hgvmate.sync.force", force);

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
                await _indexingService.IndexRepoAsync(repo.Name, deferSave: isBulkSync, cancellationToken);
                EnqueueGitNexusAnalysis(repo.Name);
                await _registry.UpdateLastSyncedAsync(repo.Name, DateTime.UtcNow);
                await _registry.ClearSyncErrorAsync(repo.Name);
                return;
            }

            await _registry.UpdateLastSyncedAsync(repo.Name, DateTime.UtcNow);

            if (isFirstSync || string.IsNullOrEmpty(oldSha))
            {
                // Re-cloned repo: skip vector re-index if SHA unchanged and vectors already cached
                if (!force && isFirstSync && newSha == oldSha && _indexingService.HasVectorsForRepo(repo.Name))
                {
                    _logger.LogInformation(
                        "Repo '{Name}' re-cloned but unchanged (SHA: {Sha}). Skipping vector re-index.",
                        repo.Name, newSha);
                    // GitNexus index is ephemeral; always rebuild it
                    EnqueueGitNexusAnalysis(repo.Name);
                    await SetLastShaAsync(repo.Name, newSha, deferredShas);
                }
                else
                {
                    // First sync or no cached vectors: full index
                    await _indexingService.IndexRepoAsync(repo.Name, deferSave: isBulkSync, cancellationToken);
                    EnqueueGitNexusAnalysis(repo.Name);
                    await SetLastShaAsync(repo.Name, newSha, deferredShas);
                }
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
                    // Incremental syncs always save immediately (low cost, only changed files)
                    await _indexingService.SaveVectorStoreAsync();

                    // Only re-analyze with GitNexus if structural (AST-parseable) files changed
                    if (HasStructuralChanges(changedFiles))
                        EnqueueGitNexusAnalysis(repo.Name);
                    else
                        _logger.LogInformation("No structural file changes in '{Name}'. Skipping GitNexus re-analysis.", repo.Name);
                }
                else
                {
                    // Diff failed or returned nothing (e.g. shallow history); fall back to full re-index
                    _logger.LogInformation("Could not determine changed files for '{Name}'; falling back to full re-index.", repo.Name);
                    await _indexingService.IndexRepoAsync(repo.Name, deferSave: isBulkSync, cancellationToken);
                    EnqueueGitNexusAnalysis(repo.Name);
                }
                await SetLastShaAsync(repo.Name, newSha, deferredShas);
            }
            else if (force)
            {
                _logger.LogInformation("Force re-index requested for repo '{Name}' (SHA: {Sha}).", repo.Name, newSha);
                await _indexingService.IndexRepoAsync(repo.Name, deferSave: isBulkSync, cancellationToken);
                EnqueueGitNexusAnalysis(repo.Name);
            }
            else
            {
                _logger.LogInformation("Repo '{Name}' is up-to-date (SHA: {Sha}). Skipping re-index.", repo.Name, newSha);
            }

            _logger.LogInformation("Repo '{Name}' synced successfully (SHA: {Sha}).", repo.Name, newSha);
            await _registry.ClearSyncErrorAsync(repo.Name);
            _telemetry?.TrackEvent("hgvmate.repo.sync_completed");
            HgvMateDiagnostics.RepoSyncTotal.Add(1, new KeyValuePair<string, object?>("repo", repo.Name), new KeyValuePair<string, object?>("status", "success"));
            activity?.SetTag("hgvmate.repo.new_sha", newSha);
        }
        catch (Exception ex)
        {
            _telemetry?.TrackException(ex);
            _telemetry?.TrackEvent("hgvmate.repo.sync_failed");
            HgvMateDiagnostics.RepoSyncErrors.Add(1, new KeyValuePair<string, object?>("repo", repo.Name));
            HgvMateDiagnostics.RepoSyncTotal.Add(1, new KeyValuePair<string, object?>("repo", repo.Name), new KeyValuePair<string, object?>("status", "error"));
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Failed to sync repo '{Name}'.", repo.Name);
            await _registry.UpdateSyncErrorAsync(repo.Name, ex.Message);
        }
        finally
        {
            HgvMateDiagnostics.RepoSyncDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("repo", repo.Name));
            _telemetry?.RecordMetric("hgvmate.repo.sync_duration_ms", sw.Elapsed.TotalMilliseconds);
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

    /// <summary>
    /// Enqueues a GitNexus analysis for <paramref name="repoName"/> to run in the background worker.
    /// This decouples ONNX embedding (CPU-bound) from GitNexus analysis (I/O-bound) so they overlap.
    /// Best-effort: uses <c>TryWrite</c> so callers are never blocked. When the 256-slot channel is
    /// full the request is dropped; the next sync cycle will re-enqueue it. GitNexus is idempotent
    /// so a missed analysis is self-healing. This avoids unbounded suspended producer tasks under
    /// backpressure.
    /// </summary>
    internal void EnqueueGitNexusAnalysis(string repoName)
    {
        if (!_gitNexusQueue.Writer.TryWrite(repoName))
            _logger.LogDebug("GitNexus queue full; analysis for '{Repo}' dropped (best-effort, will retry next cycle).", repoName);
    }

    /// <summary>
    /// Re-index only the ONNX vector embeddings for a single repository without pulling new code.
    /// </summary>
    public virtual async Task ReindexVectorsAsync(RepoRecord repo, CancellationToken cancellationToken = default)
    {
        var clonePath = GetClonePath(repo.Name);
        if (!Directory.Exists(Path.Combine(clonePath, ".git")))
            throw new DirectoryNotFoundException($"Repository '{repo.Name}' is not cloned yet.");

        _logger.LogInformation("Vector-only reindex for '{Name}'...", repo.Name);
        await _indexingService.IndexRepoAsync(repo.Name, deferSave: false, cancellationToken);
        await _indexingService.SaveVectorStoreAsync();
        _logger.LogInformation("Vector-only reindex complete for '{Name}'.", repo.Name);
    }

    /// <summary>
    /// Re-run only the GitNexus structural analysis for a single repository without pulling new code.
    /// </summary>
    public virtual async Task ReindexGitNexusAsync(RepoRecord repo, CancellationToken cancellationToken = default)
    {
        var clonePath = GetClonePath(repo.Name);
        if (!Directory.Exists(Path.Combine(clonePath, ".git")))
            throw new DirectoryNotFoundException($"Repository '{repo.Name}' is not cloned yet.");

        _logger.LogInformation("GitNexus-only reindex for '{Name}'...", repo.Name);
        await _gitNexusService.AnalyzeAsync(repo.Name, cancellationToken);
        _logger.LogInformation("GitNexus-only reindex complete for '{Name}'.", repo.Name);
    }

    /// <summary>
    /// Updates the last-known SHA for a repo. During bulk sync, defers the registry write by
    /// storing the SHA in <paramref name="deferredShas"/> (applied after the vector store flush).
    /// </summary>
    private async Task SetLastShaAsync(
        string repoName, string sha,
        System.Collections.Concurrent.ConcurrentDictionary<string, string>? deferredShas)
    {
        if (deferredShas != null)
            deferredShas[repoName] = sha;
        else
            await _registry.UpdateLastShaAsync(repoName, sha);
    }

    /// <summary>
    /// Returns true if any of the changed files are structural (AST-parseable) source files
    /// that warrant a GitNexus re-analysis.
    /// </summary>
    internal static bool HasStructuralChanges(IReadOnlyList<string> changedFiles)
        => changedFiles.Any(f => StructuralExtensions.Contains(Path.GetExtension(f)));

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
            Arguments = string.Join(" ", args.Select(a =>
                a.Contains(' ') || a.Contains('"') ? $"\"{a.Replace("\"", "\\\"")}\"" : a)),
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

    public override void Dispose()
    {
        _syncSemaphore.Dispose();
        _gitNexusSemaphore.Dispose();
        base.Dispose();
    }
}
