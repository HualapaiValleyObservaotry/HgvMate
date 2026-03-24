using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Repos;
using HgvMate.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

[TestClass]
public sealed class AdminToolsTests
{
    private FakeRepoRegistry _registry = null!;
    private FakeRepoSyncService _syncService = null!;
    private AdminTools _tools = null!;

    [TestInitialize]
    public void Setup()
    {
        _registry = new FakeRepoRegistry();
        _syncService = new FakeRepoSyncService(_registry);
        _tools = new AdminTools(_registry, _syncService);
    }

    [TestMethod]
    public async Task AddRepository_ValidArgs_ReturnsSuccess()
    {
        var result = await _tools.AddRepository("myrepo", "https://github.com/org/myrepo");
        StringAssert.Contains(result, "added");
    }

    [TestMethod]
    public async Task AddRepository_MissingName_ReturnsError()
    {
        var result = await _tools.AddRepository("", "https://github.com/org/repo");
        StringAssert.StartsWith(result, "Error:");
    }

    [TestMethod]
    public async Task AddRepository_DuplicateName_ReturnsError()
    {
        await _tools.AddRepository("myrepo", "https://github.com/org/myrepo");
        var result = await _tools.AddRepository("myrepo", "https://github.com/org/other");
        StringAssert.Contains(result, "already exists");
    }

    [TestMethod]
    public async Task AddRepository_InvalidSource_ReturnsError()
    {
        var result = await _tools.AddRepository("repo", "https://example.com/repo.git", source: "invalid");
        StringAssert.StartsWith(result, "Error:");
    }

    [TestMethod]
    public async Task RemoveRepository_ExistingRepo_ReturnsSuccess()
    {
        await _registry.AddAsync("myrepo", "https://github.com/org/myrepo", "main", "github");
        var result = await _tools.RemoveRepository("myrepo");
        StringAssert.Contains(result, "removed successfully");
    }

    [TestMethod]
    public async Task RemoveRepository_NonExistent_ReturnsError()
    {
        var result = await _tools.RemoveRepository("nonexistent");
        StringAssert.Contains(result, "not found");
    }

    [TestMethod]
    public async Task ListRepositories_NoRepos_ReturnsEmptyMessage()
    {
        var result = await _tools.ListRepositories();
        StringAssert.Contains(result, "No repositories registered");
    }

    [TestMethod]
    public async Task ListRepositories_WithRepos_ReturnsList()
    {
        await _registry.AddAsync("repo1", "https://github.com/org/repo1", "main", "github");
        var result = await _tools.ListRepositories();
        StringAssert.Contains(result, "repo1");
    }

    [TestMethod]
    public async Task IndexStatus_NoRepos_ReturnsNoReposMessage()
    {
        var result = await _tools.IndexStatus();
        StringAssert.Contains(result, "No repositories registered");
    }

    [TestMethod]
    public async Task IndexStatus_SpecificRepo_NotFound_ReturnsError()
    {
        var result = await _tools.IndexStatus("nonexistent");
        StringAssert.Contains(result, "not found");
    }

    private sealed class FakeRepoRegistry : IRepoRegistry
    {
        private readonly List<RepoRecord> _repos = [];
        private int _nextId = 1;

        public Task<RepoRecord> AddAsync(string name, string url, string branch, string source, string? addedBy = null)
        {
            var record = new RepoRecord(_nextId++, name, url, branch, source, true, null, null, addedBy);
            _repos.Add(record);
            return Task.FromResult(record);
        }

        public Task<bool> RemoveAsync(string name)
        {
            var removed = _repos.RemoveAll(r => r.Name == name) > 0;
            return Task.FromResult(removed);
        }

        public Task<IReadOnlyList<RepoRecord>> GetAllAsync()
            => Task.FromResult<IReadOnlyList<RepoRecord>>(_repos.AsReadOnly());

        public Task<RepoRecord?> GetByNameAsync(string name)
            => Task.FromResult(_repos.FirstOrDefault(r => r.Name == name));

        public Task<bool> UpdateLastShaAsync(string name, string sha)
        {
            var idx = _repos.FindIndex(r => r.Name == name);
            if (idx < 0) return Task.FromResult(false);
            _repos[idx] = _repos[idx] with { LastSha = sha };
            return Task.FromResult(true);
        }

        public Task<bool> UpdateLastSyncedAsync(string name, DateTime syncedAt)
        {
            var idx = _repos.FindIndex(r => r.Name == name);
            if (idx < 0) return Task.FromResult(false);
            _repos[idx] = _repos[idx] with { LastSynced = syncedAt.ToString("o") };
            return Task.FromResult(true);
        }

        public Task<bool> SetEnabledAsync(string name, bool enabled)
        {
            var idx = _repos.FindIndex(r => r.Name == name);
            if (idx < 0) return Task.FromResult(false);
            _repos[idx] = _repos[idx] with { Enabled = enabled };
            return Task.FromResult(true);
        }
    }

    private sealed class FakeRepoSyncService : RepoSyncService
    {
        public FakeRepoSyncService(IRepoRegistry registry)
            : base(registry,
                   new FakeCredentialProvider(),
                   new HgvMateOptions { DataPath = "./testdata" },
                   new RepoSyncOptions(),
                   NullLogger<RepoSyncService>.Instance)
        {
        }

        public override Task SyncRepoAsync(RepoRecord repo, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public override Task SyncAllAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeCredentialProvider : IGitCredentialProvider
    {
        public string? GetToken(string source) => null;
        public string BuildAuthenticatedUrl(string url, string source) => url;
    }
}
