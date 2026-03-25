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
    string? AddedBy
);

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
}
