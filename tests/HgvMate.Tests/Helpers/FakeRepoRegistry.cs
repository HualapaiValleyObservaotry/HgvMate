using System.Collections.Concurrent;
using HgvMate.Mcp.Repos;

namespace HgvMate.Tests;

internal class FakeRepoRegistry : IRepoRegistry
{
    private readonly ConcurrentBag<RepoRecord> _repos = new();
    private int _nextId;

    public Task<RepoRecord> AddAsync(string name, string url, string branch, string source, string? addedBy = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var record = new RepoRecord(id, name, url, branch, source, true, null, null, addedBy);
        _repos.Add(record);
        return Task.FromResult(record);
    }

    public Task<bool> RemoveAsync(string name)
    {
        // ConcurrentBag doesn't support removal, so rebuild
        var snapshot = _repos.ToArray();
        while (_repos.TryTake(out _)) { }
        foreach (var r in snapshot)
        {
            if (r.Name != name) _repos.Add(r);
        }
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<RepoRecord>> GetAllAsync()
        => Task.FromResult<IReadOnlyList<RepoRecord>>(_repos.ToArray());

    public Task<RepoRecord?> GetByNameAsync(string name)
        => Task.FromResult(_repos.FirstOrDefault(r => r.Name == name));

    public Task<RepoRecord?> GetByUrlAsync(string url)
        => Task.FromResult(_repos.FirstOrDefault(r =>
            JsonRepoRegistry.NormalizeUrl(r.Url) == JsonRepoRegistry.NormalizeUrl(url)));

    public virtual Task<bool> UpdateLastShaAsync(string name, string sha)
        => Task.FromResult(true);

    public Task<bool> UpdateLastSyncedAsync(string name, DateTime syncedAt)
        => Task.FromResult(true);

    public Task<bool> SetEnabledAsync(string name, bool enabled)
        => Task.FromResult(true);

    public Task<bool> UpdateSyncStateAsync(string name, string state)
        => Task.FromResult(true);

    public Task<bool> UpdateSyncErrorAsync(string name, string error)
        => Task.FromResult(true);

    public Task<bool> ClearSyncErrorAsync(string name)
        => Task.FromResult(true);
}
