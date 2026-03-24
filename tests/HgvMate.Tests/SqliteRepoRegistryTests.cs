using HgvMate.Mcp.Data;
using HgvMate.Mcp.Repos;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

[TestClass]
public sealed class SqliteRepoRegistryTests
{
    private static int _dbCounter = 0;
    private string _connStr = null!;
    private SqliteConnection _keepAlive = null!;
    private ISqliteConnectionFactory _factory = null!;
    private SqliteRepoRegistry _registry = null!;

    [TestInitialize]
    public async Task Setup()
    {
        var id = Interlocked.Increment(ref _dbCounter);
        _connStr = $"Data Source=testdb{id};Mode=Memory;Cache=Shared";

        // Keep one connection open so the in-memory DB persists
        _keepAlive = new SqliteConnection(_connStr);
        await _keepAlive.OpenAsync();

        var sql = """
            CREATE TABLE IF NOT EXISTS repositories (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                url TEXT NOT NULL,
                branch TEXT NOT NULL DEFAULT 'main',
                source TEXT NOT NULL DEFAULT 'github',
                enabled INTEGER NOT NULL DEFAULT 1,
                last_sha TEXT,
                last_synced TEXT,
                added_by TEXT
            );
            """;
        using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();

        _factory = new InMemoryConnectionFactory(_connStr);
        _registry = new SqliteRepoRegistry(_factory, NullLogger<SqliteRepoRegistry>.Instance);
    }

    [TestCleanup]
    public void Cleanup() => _keepAlive.Dispose();

    [TestMethod]
    public async Task AddAsync_ValidRepo_ReturnsRecord()
    {
        var record = await _registry.AddAsync("myrepo", "https://github.com/org/myrepo", "main", "github");
        Assert.AreEqual("myrepo", record.Name);
        Assert.AreEqual("https://github.com/org/myrepo", record.Url);
        Assert.AreEqual("main", record.Branch);
        Assert.AreEqual("github", record.Source);
        Assert.IsTrue(record.Enabled);
        Assert.IsGreaterThan(record.Id, 0);
    }

    [TestMethod]
    public async Task GetByNameAsync_ExistingRepo_ReturnsRecord()
    {
        await _registry.AddAsync("testrepo", "https://github.com/org/repo", "main", "github");
        var record = await _registry.GetByNameAsync("testrepo");
        Assert.IsNotNull(record);
        Assert.AreEqual("testrepo", record.Name);
    }

    [TestMethod]
    public async Task GetByNameAsync_NonExistentRepo_ReturnsNull()
    {
        var record = await _registry.GetByNameAsync("nonexistent");
        Assert.IsNull(record);
    }

    [TestMethod]
    public async Task GetAllAsync_MultipleRepos_ReturnsAll()
    {
        await _registry.AddAsync("repo1", "https://github.com/org/repo1", "main", "github");
        await _registry.AddAsync("repo2", "https://github.com/org/repo2", "main", "github");
        var repos = await _registry.GetAllAsync();
        Assert.HasCount(repos, 2);
    }

    [TestMethod]
    public async Task RemoveAsync_ExistingRepo_ReturnsTrue()
    {
        await _registry.AddAsync("todelete", "https://github.com/org/todelete", "main", "github");
        var removed = await _registry.RemoveAsync("todelete");
        Assert.IsTrue(removed);
        var record = await _registry.GetByNameAsync("todelete");
        Assert.IsNull(record);
    }

    [TestMethod]
    public async Task RemoveAsync_NonExistentRepo_ReturnsFalse()
    {
        var removed = await _registry.RemoveAsync("nonexistent");
        Assert.IsFalse(removed);
    }

    [TestMethod]
    public async Task UpdateLastShaAsync_ValidRepo_UpdatesSha()
    {
        await _registry.AddAsync("shatest", "https://github.com/org/shatest", "main", "github");
        var updated = await _registry.UpdateLastShaAsync("shatest", "abc123def456");
        Assert.IsTrue(updated);
        var record = await _registry.GetByNameAsync("shatest");
        Assert.AreEqual("abc123def456", record!.LastSha);
    }

    [TestMethod]
    public async Task SetEnabledAsync_ValidRepo_UpdatesEnabled()
    {
        await _registry.AddAsync("enabletest", "https://github.com/org/enabletest", "main", "github");
        await _registry.SetEnabledAsync("enabletest", false);
        var record = await _registry.GetByNameAsync("enabletest");
        Assert.IsFalse(record!.Enabled);
    }

    private sealed class InMemoryConnectionFactory : ISqliteConnectionFactory
    {
        private readonly string _connStr;
        public InMemoryConnectionFactory(string connStr) => _connStr = connStr;
        public SqliteConnection CreateConnection() => new SqliteConnection(_connStr);
    }
}
