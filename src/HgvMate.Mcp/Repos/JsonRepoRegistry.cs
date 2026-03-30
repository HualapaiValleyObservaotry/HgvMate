using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp.Repos;

/// <summary>
/// File-backed repository registry. Each repo is a JSON file: {dataPath}/repo-meta/{name}.json.
/// Thread-safe via a <see cref="SemaphoreSlim"/> for writes and concurrent reads from disk.
/// </summary>
public sealed class JsonRepoRegistry : IRepoRegistry, IDisposable
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

        // Seed the next ID from the highest existing ID (sync is acceptable in constructor)
        _nextId = LoadAllForInit().Select(r => r.Id).DefaultIfEmpty(0).Max();
    }

    public async Task<RepoRecord> AddAsync(string name, string url, string branch, string source, string? addedBy = null)
    {
        await _writeLock.WaitAsync();
        try
        {
            var path = GetFilePath(name);
            if (File.Exists(path))
            {
                _logger.LogWarning("Attempted to add repository {Name}, but a repository with that name already exists.", name);
                throw new InvalidOperationException($"A repository with the name '{name}' already exists.");
            }

            var id = Interlocked.Increment(ref _nextId);
            var record = new RepoRecord(id, name, url, branch, source, true, null, null, addedBy);
            await WriteRecordAsync(path, record);
            _logger.LogInformation("Added repository {Name} (id={Id}).", name, id);
            return record;
        }
        finally
        {
            _writeLock.Release();
        }
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

    public async Task<IReadOnlyList<RepoRecord>> GetAllAsync()
    {
        var records = (await LoadAllAsync()).OrderBy(r => r.Name).ToList();
        return records;
    }

    public async Task<RepoRecord?> GetByNameAsync(string name)
    {
        var path = GetFilePath(name);
        if (!File.Exists(path))
            return null;

        return await LoadRecordAsync(path);
    }

    public async Task<RepoRecord?> GetByUrlAsync(string url)
    {
        var normalized = NormalizeUrl(url);
        var all = await LoadAllAsync();
        return all.FirstOrDefault(r => NormalizeUrl(r.Url) == normalized);
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
        var sanitized = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));

        // If the name contained invalid characters, append a short hash to avoid collisions
        // (e.g. "a/b" and "a?b" would both sanitize to "a_b" without this).
        if (sanitized != name)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
            sanitized = $"{sanitized}_{hash}";
        }

        return sanitized;
    }

    private async Task<bool> UpdateAsync(string name, Func<RepoRecord, RepoRecord> mutate)
    {
        var path = GetFilePath(name);
        if (!File.Exists(path))
            return false;

        await _writeLock.WaitAsync();
        try
        {
            var record = await LoadRecordAsync(path);
            if (record is null) return false;
            var updated = mutate(record);
            await WriteRecordAsync(path, updated);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task WriteRecordAsync(string path, RepoRecord record)
    {
        var json = JsonSerializer.Serialize(record, JsonOptions);
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    private async Task<RepoRecord?> LoadRecordAsync(string path)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<RepoRecord>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load repo record from '{Path}'. Skipping file.", path);
            return null;
        }
    }

    private async Task<List<RepoRecord>> LoadAllAsync()
    {
        if (!Directory.Exists(_metaDir))
            return [];

        var results = new List<RepoRecord>();
        foreach (var file in Directory.EnumerateFiles(_metaDir, "*.json"))
        {
            var record = await LoadRecordAsync(file);
            if (record is not null)
                results.Add(record);
        }
        return results;
    }

    /// <summary>Synchronous load used only during constructor initialization.</summary>
    private List<RepoRecord> LoadAllForInit()
    {
        if (!Directory.Exists(_metaDir))
            return [];

        var results = new List<RepoRecord>();
        foreach (var file in Directory.EnumerateFiles(_metaDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var record = JsonSerializer.Deserialize<RepoRecord>(json, JsonOptions);
                if (record is not null)
                    results.Add(record);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load repo record from '{Path}'. Skipping file.", file);
            }
        }
        return results;
    }

    public void Dispose() => _writeLock.Dispose();
}
