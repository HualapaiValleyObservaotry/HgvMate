using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp.Repos;

/// <summary>
/// File-backed repository registry. Each repo is a JSON file: {dataPath}/repo-meta/{name}.json.
/// Thread-safe via a <see cref="SemaphoreSlim"/> for writes and concurrent reads from disk.
/// </summary>
public sealed class JsonRepoRegistry : IRepoRegistry
{
    private readonly string _metaDir;
    private readonly ILogger<JsonRepoRegistry> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _nextId;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public JsonRepoRegistry(string dataPath, ILogger<JsonRepoRegistry> logger)
    {
        _metaDir = Path.Combine(dataPath, "repo-meta");
        Directory.CreateDirectory(_metaDir);
        _logger = logger;

        // Seed the next ID from the highest existing ID
        _nextId = LoadAllSync().Select(r => r.Id).DefaultIfEmpty(0).Max();
    }

    public async Task<RepoRecord> AddAsync(string name, string url, string branch, string source, string? addedBy = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var record = new RepoRecord(id, name, url, branch, source, true, null, null, addedBy);
        await SaveRecordAsync(record);
        _logger.LogInformation("Added repository {Name} (id={Id}).", name, id);
        return record;
    }

    public async Task<bool> RemoveAsync(string name)
    {
        var path = GetFilePath(name);
        if (!File.Exists(path))
            return false;

        await _writeLock.WaitAsync();
        try
        {
            File.Delete(path);
        }
        finally
        {
            _writeLock.Release();
        }

        _logger.LogInformation("Removed repository {Name}.", name);
        return true;
    }

    public Task<IReadOnlyList<RepoRecord>> GetAllAsync()
    {
        var records = LoadAllSync().OrderBy(r => r.Name).ToList();
        return Task.FromResult<IReadOnlyList<RepoRecord>>(records);
    }

    public Task<RepoRecord?> GetByNameAsync(string name)
    {
        var path = GetFilePath(name);
        if (!File.Exists(path))
            return Task.FromResult<RepoRecord?>(null);

        var record = LoadRecord(path);
        return Task.FromResult<RepoRecord?>(record);
    }

    public Task<RepoRecord?> GetByUrlAsync(string url)
    {
        var normalized = NormalizeUrl(url);
        var match = LoadAllSync().FirstOrDefault(r => NormalizeUrl(r.Url) == normalized);
        return Task.FromResult(match);
    }

    public Task<bool> UpdateLastShaAsync(string name, string sha) =>
        UpdateAsync(name, r => r with { LastSha = sha });

    public Task<bool> UpdateLastSyncedAsync(string name, DateTime syncedAt) =>
        UpdateAsync(name, r => r with { LastSynced = syncedAt.ToString("o") });

    public Task<bool> SetEnabledAsync(string name, bool enabled) =>
        UpdateAsync(name, r => r with { Enabled = enabled });

    public Task<bool> UpdateSyncStateAsync(string name, string state) =>
        UpdateAsync(name, r => r with { SyncState = state });

    public Task<bool> UpdateSyncErrorAsync(string name, string error) =>
        UpdateAsync(name, r => r with
        {
            SyncState = SyncStates.Failed,
            LastError = error,
            LastErrorAt = DateTime.UtcNow.ToString("o"),
            FailedSyncCount = r.FailedSyncCount + 1,
        });

    public Task<bool> ClearSyncErrorAsync(string name) =>
        UpdateAsync(name, r => r with
        {
            SyncState = SyncStates.Synced,
            LastError = null,
            LastErrorAt = null,
            FailedSyncCount = 0,
        });

    // ── URL normalization (moved from SqliteRepoRegistry) ───────────────

    internal static string NormalizeUrl(string url)
    {
        var normalized = url.Trim().ToLowerInvariant();
        if (normalized.EndsWith(".git"))
            normalized = normalized[..^4];
        normalized = normalized.TrimEnd('/');
        normalized = normalized.Replace("http://", "https://");
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            normalized = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".TrimEnd('/');
        return normalized;
    }

    // ── Private helpers ─────────────────────────────────────────────────

    private string GetFilePath(string name) =>
        Path.Combine(_metaDir, SanitizeFileName(name) + ".json");

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }

    private async Task<bool> UpdateAsync(string name, Func<RepoRecord, RepoRecord> mutate)
    {
        var path = GetFilePath(name);
        if (!File.Exists(path))
            return false;

        await _writeLock.WaitAsync();
        try
        {
            var record = LoadRecord(path);
            if (record is null) return false;
            var updated = mutate(record);
            WriteRecord(path, updated);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SaveRecordAsync(RepoRecord record)
    {
        var path = GetFilePath(record.Name);
        await _writeLock.WaitAsync();
        try
        {
            WriteRecord(path, record);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void WriteRecord(string path, RepoRecord record)
    {
        var json = JsonSerializer.Serialize(record, JsonOptions);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    private static RepoRecord? LoadRecord(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<RepoRecord>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private List<RepoRecord> LoadAllSync()
    {
        if (!Directory.Exists(_metaDir))
            return [];

        var results = new List<RepoRecord>();
        foreach (var file in Directory.EnumerateFiles(_metaDir, "*.json"))
        {
            var record = LoadRecord(file);
            if (record is not null)
                results.Add(record);
        }
        return results;
    }
}
