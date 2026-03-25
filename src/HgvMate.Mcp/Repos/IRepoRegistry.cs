namespace HgvMate.Mcp.Repos;

public record RepoRecord(
    int Id,
    string Name,
    string Url,
    string Branch,
    string Source,
    bool Enabled,
    string? LastSha,
    string? LastSynced,
    string? AddedBy,
    string? LastError = null,
    string? LastErrorAt = null,
    int FailedSyncCount = 0,
    string SyncState = SyncStates.Pending
);

public static class SyncStates
{
    public const string Pending = "pending";
    public const string Syncing = "syncing";
    public const string Synced = "synced";
    public const string Failed = "failed";
}

public interface IRepoRegistry
{
    Task<RepoRecord> AddAsync(string name, string url, string branch, string source, string? addedBy = null);
    Task<bool> RemoveAsync(string name);
    Task<IReadOnlyList<RepoRecord>> GetAllAsync();
    Task<RepoRecord?> GetByNameAsync(string name);
    Task<RepoRecord?> GetByUrlAsync(string url);
    Task<bool> UpdateLastShaAsync(string name, string sha);
    Task<bool> UpdateLastSyncedAsync(string name, DateTime syncedAt);
    Task<bool> SetEnabledAsync(string name, bool enabled);
    Task<bool> UpdateSyncStateAsync(string name, string state);
    Task<bool> UpdateSyncErrorAsync(string name, string error);
    Task<bool> ClearSyncErrorAsync(string name);
}
