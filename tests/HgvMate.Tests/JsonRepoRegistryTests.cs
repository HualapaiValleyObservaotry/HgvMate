using HgvMate.Mcp.Repos;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

[TestClass]
public sealed class JsonRepoRegistryTests
{
    private string _tempDir = null!;
    private JsonRepoRegistry _registry = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "HgvMateJsonReg_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _registry = new JsonRepoRegistry(_tempDir, NullLogger<JsonRepoRegistry>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    [TestCategory("Unit")]
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
    [TestCategory("Unit")]
    public async Task GetByNameAsync_ExistingRepo_ReturnsRecord()
    {
        await _registry.AddAsync("testrepo", "https://github.com/org/repo", "main", "github");
        var record = await _registry.GetByNameAsync("testrepo");
        Assert.IsNotNull(record);
        Assert.AreEqual("testrepo", record.Name);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetByNameAsync_NonExistentRepo_ReturnsNull()
    {
        var record = await _registry.GetByNameAsync("nonexistent");
        Assert.IsNull(record);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetAllAsync_MultipleRepos_ReturnsAll()
    {
        await _registry.AddAsync("repo1", "https://github.com/org/repo1", "main", "github");
        await _registry.AddAsync("repo2", "https://github.com/org/repo2", "main", "github");
        var repos = await _registry.GetAllAsync();
        Assert.HasCount(2, repos);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetAllAsync_ReturnsOrderedByName()
    {
        await _registry.AddAsync("zrepo", "https://github.com/org/zrepo", "main", "github");
        await _registry.AddAsync("arepo", "https://github.com/org/arepo", "main", "github");
        var repos = await _registry.GetAllAsync();
        Assert.AreEqual("arepo", repos[0].Name);
        Assert.AreEqual("zrepo", repos[1].Name);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RemoveAsync_ExistingRepo_ReturnsTrue()
    {
        await _registry.AddAsync("todelete", "https://github.com/org/todelete", "main", "github");
        var removed = await _registry.RemoveAsync("todelete");
        Assert.IsTrue(removed);
        var record = await _registry.GetByNameAsync("todelete");
        Assert.IsNull(record);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RemoveAsync_NonExistentRepo_ReturnsFalse()
    {
        var removed = await _registry.RemoveAsync("nonexistent");
        Assert.IsFalse(removed);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task UpdateLastShaAsync_ValidRepo_UpdatesSha()
    {
        await _registry.AddAsync("shatest", "https://github.com/org/shatest", "main", "github");
        var updated = await _registry.UpdateLastShaAsync("shatest", "abc123def456");
        Assert.IsTrue(updated);
        var record = await _registry.GetByNameAsync("shatest");
        Assert.AreEqual("abc123def456", record!.LastSha);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SetEnabledAsync_ValidRepo_UpdatesEnabled()
    {
        await _registry.AddAsync("enabletest", "https://github.com/org/enabletest", "main", "github");
        await _registry.SetEnabledAsync("enabletest", false);
        var record = await _registry.GetByNameAsync("enabletest");
        Assert.IsFalse(record!.Enabled);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetByUrlAsync_ExactMatch_ReturnsRecord()
    {
        await _registry.AddAsync("repo1", "https://github.com/org/myrepo.git", "main", "github");
        var result = await _registry.GetByUrlAsync("https://github.com/org/myrepo.git");
        Assert.IsNotNull(result);
        Assert.AreEqual("repo1", result.Name);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetByUrlAsync_NormalizedMatch_StripsTrailingGit()
    {
        await _registry.AddAsync("repo1", "https://github.com/org/myrepo.git", "main", "github");
        var result = await _registry.GetByUrlAsync("https://github.com/org/myrepo");
        Assert.IsNotNull(result);
        Assert.AreEqual("repo1", result.Name);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetByUrlAsync_NonExistent_ReturnsNull()
    {
        var result = await _registry.GetByUrlAsync("https://github.com/org/nonexistent");
        Assert.IsNull(result);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void NormalizeUrl_StripsGitSuffix()
    {
        Assert.AreEqual(
            JsonRepoRegistry.NormalizeUrl("https://github.com/org/repo.git"),
            JsonRepoRegistry.NormalizeUrl("https://github.com/org/repo"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void NormalizeUrl_StripsTrailingSlash()
    {
        Assert.AreEqual(
            JsonRepoRegistry.NormalizeUrl("https://github.com/org/repo/"),
            JsonRepoRegistry.NormalizeUrl("https://github.com/org/repo"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void NormalizeUrl_NormalizesProtocol()
    {
        Assert.AreEqual(
            JsonRepoRegistry.NormalizeUrl("http://github.com/org/repo"),
            JsonRepoRegistry.NormalizeUrl("https://github.com/org/repo"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void NormalizeUrl_StripsEmbeddedCredentials()
    {
        Assert.AreEqual(
            JsonRepoRegistry.NormalizeUrl("https://user:token@github.com/org/repo"),
            JsonRepoRegistry.NormalizeUrl("https://github.com/org/repo"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task AddAsync_NewRepo_HasPendingSyncState()
    {
        var record = await _registry.AddAsync("statetest", "https://github.com/org/statetest", "main", "github");
        Assert.AreEqual(SyncStates.Pending, record.SyncState);
        Assert.IsNull(record.LastError);
        Assert.AreEqual(0, record.FailedSyncCount);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task UpdateSyncStateAsync_TransitionsState()
    {
        await _registry.AddAsync("sync1", "https://github.com/org/sync1", "main", "github");
        var updated = await _registry.UpdateSyncStateAsync("sync1", SyncStates.Syncing);
        Assert.IsTrue(updated);
        var record = await _registry.GetByNameAsync("sync1");
        Assert.AreEqual(SyncStates.Syncing, record!.SyncState);
    }

    [TestMethod]
    [TestCategory("Unit")]
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
    [TestCategory("Unit")]
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
    [TestCategory("Unit")]
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
    [TestCategory("Unit")]
    public async Task AddAsync_SetsAddedBy()
    {
        var record = await _registry.AddAsync("addedbytest", "https://github.com/org/addedbytest", "main", "github", "mcp-tool");
        Assert.AreEqual("mcp-tool", record.AddedBy);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task PersistsToDisk_SurvivesNewInstance()
    {
        await _registry.AddAsync("persist", "https://github.com/org/persist", "main", "github");

        // Create a new registry instance pointing at the same directory
        var registry2 = new JsonRepoRegistry(_tempDir, NullLogger<JsonRepoRegistry>.Instance);
        var record = await registry2.GetByNameAsync("persist");
        Assert.IsNotNull(record);
        Assert.AreEqual("persist", record.Name);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task UpdateAsync_NonExistentRepo_ReturnsFalse()
    {
        var result = await _registry.UpdateLastShaAsync("ghost", "sha123");
        Assert.IsFalse(result);
    }
}
