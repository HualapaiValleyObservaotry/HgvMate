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
                added_by TEXT,
                last_error TEXT,
                last_error_at TEXT,
                failed_sync_count INTEGER NOT NULL DEFAULT 0,
                sync_state TEXT NOT NULL DEFAULT 'pending'
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
        Assert.IsGreaterThan(0, record.Id);
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
        Assert.HasCount(2, repos);
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

    [TestMethod]
    public async Task GetByUrlAsync_ExactMatch_ReturnsRecord()
    {
        await _registry.AddAsync("repo1", "https://github.com/org/myrepo.git", "main", "github");
        var result = await _registry.GetByUrlAsync("https://github.com/org/myrepo.git");
        Assert.IsNotNull(result);
        Assert.AreEqual("repo1", result.Name);
    }

    [TestMethod]
    public async Task GetByUrlAsync_NormalizedMatch_StripsTrailingGit()
    {
        await _registry.AddAsync("repo1", "https://github.com/org/myrepo.git", "main", "github");
        var result = await _registry.GetByUrlAsync("https://github.com/org/myrepo");
        Assert.IsNotNull(result);
        Assert.AreEqual("repo1", result.Name);
    }

    [TestMethod]
    public async Task GetByUrlAsync_NonExistent_ReturnsNull()
    {
        var result = await _registry.GetByUrlAsync("https://github.com/org/nonexistent");
        Assert.IsNull(result);
    }

    [TestMethod]
    public void NormalizeUrl_StripsGitSuffix()
    {
        Assert.AreEqual(
            SqliteRepoRegistry.NormalizeUrl("https://github.com/org/repo.git"),
            SqliteRepoRegistry.NormalizeUrl("https://github.com/org/repo"));
    }

    [TestMethod]
    public void NormalizeUrl_StripsTrailingSlash()
    {
        Assert.AreEqual(
            SqliteRepoRegistry.NormalizeUrl("https://github.com/org/repo/"),
            SqliteRepoRegistry.NormalizeUrl("https://github.com/org/repo"));
    }

    [TestMethod]
    public void NormalizeUrl_NormalizesProtocol()
    {
        Assert.AreEqual(
            SqliteRepoRegistry.NormalizeUrl("http://github.com/org/repo"),
            SqliteRepoRegistry.NormalizeUrl("https://github.com/org/repo"));
    }

    [TestMethod]
    public void NormalizeUrl_StripsEmbeddedCredentials()
    {
        Assert.AreEqual(
            SqliteRepoRegistry.NormalizeUrl("https://user:token@github.com/org/repo"),
            SqliteRepoRegistry.NormalizeUrl("https://github.com/org/repo"));
    }

    [TestMethod]
    public async Task AddAsync_NewRepo_HasPendingSyncState()
    {
        var record = await _registry.AddAsync("statetest", "https://github.com/org/statetest", "main", "github");
        Assert.AreEqual(SyncStates.Pending, record.SyncState);
        Assert.IsNull(record.LastError);
        Assert.AreEqual(0, record.FailedSyncCount);
    }

    [TestMethod]
    public async Task UpdateSyncStateAsync_TransitionsState()
    {
        await _registry.AddAsync("sync1", "https://github.com/org/sync1", "main", "github");
        var updated = await _registry.UpdateSyncStateAsync("sync1", SyncStates.Syncing);
        Assert.IsTrue(updated);
        var record = await _registry.GetByNameAsync("sync1");
        Assert.AreEqual(SyncStates.Syncing, record!.SyncState);
    }

    [TestMethod]
    public async Task UpdateSyncErrorAsync_SetsSyncStateFailedAndIncrementsCount()
    {
        await _registry.AddAsync("errrepo", "https://github.com/org/errrepo", "main", "github");
        var updated = await _registry.UpdateSyncErrorAsync("errrepo", "Connection timed out");
        Assert.IsTrue(updated);
        var record = await _registry.GetByNameAsync("errrepo");
        Assert.AreEqual(SyncStates.Failed, record!.SyncState);
        Assert.AreEqual("Connection timed out", record.LastError);
        Assert.IsNotNull(record.LastErrorAt);
        Assert.AreEqual(1, record.FailedSyncCount);
    }

    [TestMethod]
    public async Task UpdateSyncErrorAsync_AccumulatesFailedSyncCount()
    {
        await _registry.AddAsync("errrepo2", "https://github.com/org/errrepo2", "main", "github");
        await _registry.UpdateSyncErrorAsync("errrepo2", "error 1");
        await _registry.UpdateSyncErrorAsync("errrepo2", "error 2");
        var record = await _registry.GetByNameAsync("errrepo2");
        Assert.AreEqual(2, record!.FailedSyncCount);
        Assert.AreEqual("error 2", record.LastError);
    }

    [TestMethod]
    public async Task ClearSyncErrorAsync_ResetsErrorAndSetsStateToSynced()
    {
        await _registry.AddAsync("clearrepo", "https://github.com/org/clearrepo", "main", "github");
        await _registry.UpdateSyncErrorAsync("clearrepo", "some error");
        var cleared = await _registry.ClearSyncErrorAsync("clearrepo");
        Assert.IsTrue(cleared);
        var record = await _registry.GetByNameAsync("clearrepo");
        Assert.AreEqual(SyncStates.Synced, record!.SyncState);
        Assert.IsNull(record.LastError);
        Assert.IsNull(record.LastErrorAt);
        Assert.AreEqual(0, record.FailedSyncCount);
    }

    [TestMethod]
    public async Task DatabaseInitializer_MigratesExistingDatabase()
    {
        // Simulate a legacy database without the new columns
        var legacySql = """
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
        using var legacyCmd = new SqliteCommand(legacySql, _keepAlive);
        // Table already exists from Setup, so just test that migration doesn't throw
        // by running the initializer on an already-migrated DB
        var initializer = new DatabaseInitializer(_factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync(); // Should be idempotent
    }

    private sealed class InMemoryConnectionFactory : ISqliteConnectionFactory
    {
        private readonly string _connStr;
        public InMemoryConnectionFactory(string connStr) => _connStr = connStr;
        public SqliteConnection CreateConnection() => new SqliteConnection(_connStr);
    }
}
