using HgvMate.Mcp.Repos;

namespace HgvMate.Tests;

internal class FakeRepoRegistry : IRepoRegistry
{
    private readonly List<RepoRecord> _repos = new();

    public Task<RepoRecord> AddAsync(string name, string url, string branch, string source, string? addedBy = null)
    {
        var record = new RepoRecord(
            _repos.Count + 1, name, url, branch, source, true, null, null, addedBy);
        _repos.Add(record);
        return Task.FromResult(record);
    }

    public Task<bool> RemoveAsync(string name)
    {
        _repos.RemoveAll(r => r.Name == name);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<RepoRecord>> GetAllAsync()
        => Task.FromResult<IReadOnlyList<RepoRecord>>(_repos);

    public Task<RepoRecord?> GetByNameAsync(string name)
        => Task.FromResult(_repos.FirstOrDefault(r => r.Name == name));

    public virtual Task<bool> UpdateLastShaAsync(string name, string sha)
        => Task.FromResult(true);

    public Task<bool> UpdateLastSyncedAsync(string name, DateTime syncedAt)
        => Task.FromResult(true);

    public Task<bool> SetEnabledAsync(string name, bool enabled)
        => Task.FromResult(true);
}
