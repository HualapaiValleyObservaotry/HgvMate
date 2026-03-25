using System.Collections.Concurrent;
using HgvMate.Mcp.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp.Search;

public record SourceChunk(
    string RepoName,
    string FilePath,
    int ChunkIndex,
    string Content,
    float[] Embedding
);

public record VectorSearchResult(string RepoName, string FilePath, int ChunkIndex, string Content, float Score);

public class VectorStore
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ILogger<VectorStore> _logger;

    // In-memory cache: keyed by (repo_name, file_path, chunk_index)
    private readonly ConcurrentDictionary<(string Repo, string File, int Index), CachedChunk> _cache = new();
    private volatile bool _cacheLoaded;

    internal record CachedChunk(string Content, float[] Embedding);

    public VectorStore(ISqliteConnectionFactory connectionFactory, ILogger<VectorStore> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync()
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();

        // Check if the table already exists and needs migration from JSON to BLOB
        var needsMigration = false;
        var tableExists = false;

        using (var checkCmd = new SqliteCommand(
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='source_chunks';", conn))
        {
            var result = await checkCmd.ExecuteScalarAsync();
            if (result is string sql)
            {
                tableExists = true;
                // Old schema stored embeddings as TEXT; new schema uses BLOB
                needsMigration = sql.Contains("embedding TEXT", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (needsMigration)
        {
            _logger.LogInformation("Migrating source_chunks from TEXT embeddings to BLOB...");
            await MigrateTextToBlobAsync(conn);
        }
        else if (!tableExists)
        {
            const string createSql = """
                CREATE TABLE IF NOT EXISTS source_chunks (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    repo_name TEXT NOT NULL,
                    file_path TEXT NOT NULL,
                    chunk_index INTEGER NOT NULL,
                    content TEXT NOT NULL,
                    embedding BLOB NOT NULL,
                    UNIQUE(repo_name, file_path, chunk_index)
                );
                CREATE INDEX IF NOT EXISTS idx_chunks_repo ON source_chunks(repo_name);
                CREATE INDEX IF NOT EXISTS idx_chunks_file ON source_chunks(repo_name, file_path);
                """;
            using var cmd = new SqliteCommand(createSql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        await LoadCacheAsync();
    }

    public async Task LoadCacheAsync()
    {
        _cache.Clear();
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();

        const string sql = "SELECT repo_name, file_path, chunk_index, content, embedding FROM source_chunks;";
        using var cmd = new SqliteCommand(sql, conn);
        using var reader = await cmd.ExecuteReaderAsync();
        int count = 0;
        while (await reader.ReadAsync())
        {
            var key = (reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
            var blob = (byte[])reader[4];
            _cache[key] = new CachedChunk(reader.GetString(3), BlobToFloats(blob));
            count++;
        }

        _cacheLoaded = true;
        _logger.LogInformation("Vector cache loaded: {Count} chunks ({SizeMb:F1} MB estimated).",
            count, count * 5.0 / 1024);
    }

    private async Task MigrateTextToBlobAsync(SqliteConnection conn)
    {
        // Rename old table, create new with BLOB, migrate data, drop old
        using var tx = conn.BeginTransaction();
        try
        {
            using (var cmd = new SqliteCommand("ALTER TABLE source_chunks RENAME TO source_chunks_old;", conn, tx))
                await cmd.ExecuteNonQueryAsync();

            using (var cmd = new SqliteCommand("""
                CREATE TABLE source_chunks (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    repo_name TEXT NOT NULL,
                    file_path TEXT NOT NULL,
                    chunk_index INTEGER NOT NULL,
                    content TEXT NOT NULL,
                    embedding BLOB NOT NULL,
                    UNIQUE(repo_name, file_path, chunk_index)
                );
                CREATE INDEX IF NOT EXISTS idx_chunks_repo ON source_chunks(repo_name);
                CREATE INDEX IF NOT EXISTS idx_chunks_file ON source_chunks(repo_name, file_path);
                """, conn, tx))
                await cmd.ExecuteNonQueryAsync();

            // Read old JSON embeddings and write as BLOB
            using (var readCmd = new SqliteCommand(
                "SELECT repo_name, file_path, chunk_index, content, embedding FROM source_chunks_old;", conn, tx))
            using (var reader = await readCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var jsonEmbedding = reader.GetString(4);
                    var floats = System.Text.Json.JsonSerializer.Deserialize<float[]>(jsonEmbedding) ?? [];
                    using var writeCmd = new SqliteCommand("""
                        INSERT INTO source_chunks (repo_name, file_path, chunk_index, content, embedding)
                        VALUES (@repo, @file, @idx, @content, @embedding);
                        """, conn, tx);
                    writeCmd.Parameters.AddWithValue("@repo", reader.GetString(0));
                    writeCmd.Parameters.AddWithValue("@file", reader.GetString(1));
                    writeCmd.Parameters.AddWithValue("@idx", reader.GetInt32(2));
                    writeCmd.Parameters.AddWithValue("@content", reader.GetString(3));
                    writeCmd.Parameters.AddWithValue("@embedding", FloatsToBlob(floats));
                    await writeCmd.ExecuteNonQueryAsync();
                }
            }

            using (var cmd = new SqliteCommand("DROP TABLE source_chunks_old;", conn, tx))
                await cmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
            _logger.LogInformation("Migration complete.");
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task UpsertChunkAsync(SourceChunk chunk)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        const string sql = """
            INSERT INTO source_chunks (repo_name, file_path, chunk_index, content, embedding)
            VALUES (@repo, @file, @idx, @content, @embedding)
            ON CONFLICT(repo_name, file_path, chunk_index) DO UPDATE SET
                content = excluded.content,
                embedding = excluded.embedding;
            """;
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@repo", chunk.RepoName);
        cmd.Parameters.AddWithValue("@file", chunk.FilePath);
        cmd.Parameters.AddWithValue("@idx", chunk.ChunkIndex);
        cmd.Parameters.AddWithValue("@content", chunk.Content);
        cmd.Parameters.AddWithValue("@embedding", FloatsToBlob(chunk.Embedding));
        await cmd.ExecuteNonQueryAsync();

        _cache[(chunk.RepoName, chunk.FilePath, chunk.ChunkIndex)] =
            new CachedChunk(chunk.Content, chunk.Embedding);
    }

    public async Task UpsertChunksAsync(IEnumerable<SourceChunk> chunks)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        const string sql = """
            INSERT INTO source_chunks (repo_name, file_path, chunk_index, content, embedding)
            VALUES (@repo, @file, @idx, @content, @embedding)
            ON CONFLICT(repo_name, file_path, chunk_index) DO UPDATE SET
                content = excluded.content,
                embedding = excluded.embedding;
            """;
        foreach (var chunk in chunks)
        {
            using var cmd = new SqliteCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@repo", chunk.RepoName);
            cmd.Parameters.AddWithValue("@file", chunk.FilePath);
            cmd.Parameters.AddWithValue("@idx", chunk.ChunkIndex);
            cmd.Parameters.AddWithValue("@content", chunk.Content);
            cmd.Parameters.AddWithValue("@embedding", FloatsToBlob(chunk.Embedding));
            await cmd.ExecuteNonQueryAsync();

            _cache[(chunk.RepoName, chunk.FilePath, chunk.ChunkIndex)] =
                new CachedChunk(chunk.Content, chunk.Embedding);
        }
        await tx.CommitAsync();
    }

    public async Task DeleteChunksForFileAsync(string repoName, string filePath)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        const string sql = "DELETE FROM source_chunks WHERE repo_name = @repo AND file_path = @file;";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@repo", repoName);
        cmd.Parameters.AddWithValue("@file", filePath);
        await cmd.ExecuteNonQueryAsync();

        // Evict from cache
        var keysToRemove = _cache.Keys.Where(k => k.Repo == repoName && k.File == filePath).ToList();
        foreach (var key in keysToRemove)
            _cache.TryRemove(key, out _);
    }

    public async Task DeleteChunksForRepoAsync(string repoName)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        const string sql = "DELETE FROM source_chunks WHERE repo_name = @repo;";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@repo", repoName);
        await cmd.ExecuteNonQueryAsync();

        // Evict from cache
        var keysToRemove = _cache.Keys.Where(k => k.Repo == repoName).ToList();
        foreach (var key in keysToRemove)
            _cache.TryRemove(key, out _);
    }

    public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        float[] queryVector,
        string? repoName = null,
        int limit = 20)
    {
        if (_cacheLoaded)
            return Task.FromResult(SearchFromCache(queryVector, repoName, limit));

        return SearchFromDatabaseAsync(queryVector, repoName, limit);
    }

    private IReadOnlyList<VectorSearchResult> SearchFromCache(
        float[] queryVector,
        string? repoName,
        int limit)
    {
        var entries = repoName != null
            ? _cache.Where(kv => kv.Key.Repo == repoName)
            : _cache;

        var results = new List<(string Repo, string File, int Index, string Content, float Score)>();
        foreach (var kv in entries)
        {
            var score = CosineSimilarity(queryVector, kv.Value.Embedding);
            results.Add((kv.Key.Repo, kv.Key.File, kv.Key.Index, kv.Value.Content, score));
        }

        return results
            .OrderByDescending(c => c.Score)
            .Take(limit)
            .Select(c => new VectorSearchResult(c.Repo, c.File, c.Index, c.Content, c.Score))
            .ToList();
    }

    private async Task<IReadOnlyList<VectorSearchResult>> SearchFromDatabaseAsync(
        float[] queryVector,
        string? repoName,
        int limit)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();

        var sql = repoName != null
            ? "SELECT repo_name, file_path, chunk_index, content, embedding FROM source_chunks WHERE repo_name = @repo;"
            : "SELECT repo_name, file_path, chunk_index, content, embedding FROM source_chunks;";

        using var cmd = new SqliteCommand(sql, conn);
        if (repoName != null)
            cmd.Parameters.AddWithValue("@repo", repoName);

        var candidates = new List<(string RepoName, string FilePath, int ChunkIndex, string Content, float Score)>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var blob = (byte[])reader[4];
            var embedding = BlobToFloats(blob);
            var score = CosineSimilarity(queryVector, embedding);
            candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), score));
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .Take(limit)
            .Select(c => new VectorSearchResult(c.RepoName, c.FilePath, c.ChunkIndex, c.Content, c.Score))
            .ToList();
    }

    public Task<Dictionary<string, int>> GetChunkCountsAsync()
    {
        if (_cacheLoaded)
        {
            var counts = _cache.Keys
                .GroupBy(k => k.Repo)
                .ToDictionary(g => g.Key, g => g.Count());
            return Task.FromResult(counts);
        }

        return GetChunkCountsFromDatabaseAsync();
    }

    private async Task<Dictionary<string, int>> GetChunkCountsFromDatabaseAsync()
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        const string sql = "SELECT repo_name, COUNT(*) FROM source_chunks GROUP BY repo_name;";
        using var cmd = new SqliteCommand(sql, conn);
        using var reader = await cmd.ExecuteReaderAsync();
        var counts = new Dictionary<string, int>();
        while (await reader.ReadAsync())
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    public bool IsCacheLoaded => _cacheLoaded;
    public int CachedChunkCount => _cache.Count;
    public double EstimatedCacheSizeMb => _cache.Count * 5.0 / 1024;

    // ── Binary serialization ────────────────────────────────────────────

    internal static byte[] FloatsToBlob(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static float[] BlobToFloats(byte[] blob)
    {
        var floats = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, floats, 0, blob.Length);
        return floats;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        if (normA < 1e-12f || normB < 1e-12f) return 0f;
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }
}
