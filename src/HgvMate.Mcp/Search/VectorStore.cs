using HgvMate.Mcp.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;

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

    public VectorStore(ISqliteConnectionFactory connectionFactory, ILogger<VectorStore> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync()
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        const string sql = """
            CREATE TABLE IF NOT EXISTS source_chunks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                repo_name TEXT NOT NULL,
                file_path TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                content TEXT NOT NULL,
                embedding TEXT NOT NULL,
                UNIQUE(repo_name, file_path, chunk_index)
            );
            CREATE INDEX IF NOT EXISTS idx_chunks_repo ON source_chunks(repo_name);
            CREATE INDEX IF NOT EXISTS idx_chunks_file ON source_chunks(repo_name, file_path);
            """;
        using var cmd = new SqliteCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
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
        cmd.Parameters.AddWithValue("@embedding", JsonSerializer.Serialize(chunk.Embedding));
        await cmd.ExecuteNonQueryAsync();
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
            cmd.Parameters.AddWithValue("@embedding", JsonSerializer.Serialize(chunk.Embedding));
            await cmd.ExecuteNonQueryAsync();
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
    }

    public async Task DeleteChunksForRepoAsync(string repoName)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();
        const string sql = "DELETE FROM source_chunks WHERE repo_name = @repo;";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@repo", repoName);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        float[] queryVector,
        string? repoName = null,
        int limit = 20)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync();

        var sql = repoName != null
            ? "SELECT repo_name, file_path, chunk_index, content, embedding FROM source_chunks WHERE repo_name = @repo;"
            : "SELECT repo_name, file_path, chunk_index, content, embedding FROM source_chunks;";

        using var cmd = new SqliteCommand(sql, conn);
        if (repoName != null)
            cmd.Parameters.AddWithValue("@repo", repoName);

        var candidates = new List<(string RepoName, string FilePath, int ChunkIndex, string Content, float[] Embedding)>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var embeddingJson = reader.GetString(4);
            var embedding = JsonSerializer.Deserialize<float[]>(embeddingJson) ?? [];
            candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), embedding));
        }

        return candidates
            .Select(c => new VectorSearchResult(
                c.RepoName, c.FilePath, c.ChunkIndex, c.Content,
                CosineSimilarity(queryVector, c.Embedding)))
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToList();
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
