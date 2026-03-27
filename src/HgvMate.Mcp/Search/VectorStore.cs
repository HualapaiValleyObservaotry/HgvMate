using System.Collections.Concurrent;
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

/// <summary>
/// Stores source code embeddings in a binary file and serves searches from an in-memory cache.
/// Replaces the previous SQLite-backed implementation to eliminate lock contention on network
/// filesystems where SQLite's locking model is unreliable.
/// </summary>
public class VectorStore
{
    private static readonly byte[] Magic = "HGVM"u8.ToArray();
    private const uint FormatVersion = 1;

    private readonly string _storagePath;
    private readonly ILogger<VectorStore> _logger;
    private readonly Lock _saveLock = new();

    // In-memory cache: keyed by (repo_name, file_path, chunk_index)
    private readonly ConcurrentDictionary<(string Repo, string File, int Index), CachedChunk> _cache = new();
    private volatile bool _cacheLoaded;

    internal record CachedChunk(string Content, float[] Embedding);

    public VectorStore(string storagePath, ILogger<VectorStore> logger)
    {
        _storagePath = storagePath;
        _logger = logger;
    }

    /// <summary>
    /// Loads the vector cache from the binary file on disk.
    /// If the file does not exist, starts with an empty cache.
    /// </summary>
    public async Task LoadAsync()
    {
        _cache.Clear();

        if (!File.Exists(_storagePath))
        {
            _cacheLoaded = true;
            _logger.LogInformation("No vector file found at {Path}. Starting with empty cache.", _storagePath);
            return;
        }

        await Task.Run(() =>
        {
            using var fs = new FileStream(_storagePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024);
            using var reader = new BinaryReader(fs);

            // Header
            var magic = reader.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3])
                throw new InvalidDataException("Invalid vector file: bad magic bytes.");

            var version = reader.ReadUInt32();
            if (version != FormatVersion)
                throw new InvalidDataException($"Unsupported vector file version: {version}.");

            var embeddingDim = reader.ReadInt32();
            var chunkCount = reader.ReadInt32();

            for (var i = 0; i < chunkCount; i++)
            {
                var repo = reader.ReadString();
                var file = reader.ReadString();
                var chunkIndex = reader.ReadInt32();
                var content = reader.ReadString();

                var expectedBytes = embeddingDim * sizeof(float);
                var blob = reader.ReadBytes(expectedBytes);
                if (blob.Length != expectedBytes)
                    throw new InvalidDataException($"Truncated embedding at chunk {i}: expected {expectedBytes} bytes, got {blob.Length}.");
                var embedding = BlobToFloats(blob);

                _cache[(repo, file, chunkIndex)] = new CachedChunk(content, embedding);
            }
        });

        _cacheLoaded = true;
        _logger.LogInformation("Vector cache loaded: {Count} chunks ({SizeMb:F1} MB estimated).",
            _cache.Count, _cache.Count * 5.0 / 1024);
    }

    /// <summary>
    /// Persists the entire in-memory cache to the binary file.
    /// Writes to a temp file first, then atomically replaces the target for crash safety.
    /// </summary>
    public async Task SaveAsync()
    {
        var snapshot = _cache.ToArray();
        var embeddingDim = snapshot.Length > 0 ? snapshot[0].Value.Embedding.Length : 384;

        // Validate all embeddings have consistent dimensions
        for (int i = 0; i < snapshot.Length; i++)
        {
            if (snapshot[i].Value.Embedding.Length != embeddingDim)
                throw new InvalidOperationException(
                    $"Embedding dimension mismatch at chunk '{snapshot[i].Key.Repo}/{snapshot[i].Key.File}#{snapshot[i].Key.Index}': expected {embeddingDim}, got {snapshot[i].Value.Embedding.Length}.");
        }

        var tempPath = _storagePath + ".tmp";

        await Task.Run(() =>
        {
            lock (_saveLock)
            {
                var dir = Path.GetDirectoryName(_storagePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024))
                using (var writer = new BinaryWriter(fs))
                {
                    // Header
                    writer.Write(Magic);
                    writer.Write(FormatVersion);
                    writer.Write(embeddingDim);
                    writer.Write(snapshot.Length);

                    foreach (var kv in snapshot)
                    {
                        writer.Write(kv.Key.Repo);
                        writer.Write(kv.Key.File);
                        writer.Write(kv.Key.Index);
                        writer.Write(kv.Value.Content);
                        writer.Write(FloatsToBlob(kv.Value.Embedding));
                    }
                }

                // Atomic replace
                File.Move(tempPath, _storagePath, overwrite: true);
            }
        });

        _logger.LogInformation("Vector store saved: {Count} chunks to {Path}.", snapshot.Length, _storagePath);
    }

    public void UpsertChunk(SourceChunk chunk)
    {
        _cache[(chunk.RepoName, chunk.FilePath, chunk.ChunkIndex)] =
            new CachedChunk(chunk.Content, chunk.Embedding);
    }

    public void UpsertChunks(IEnumerable<SourceChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            _cache[(chunk.RepoName, chunk.FilePath, chunk.ChunkIndex)] =
                new CachedChunk(chunk.Content, chunk.Embedding);
        }
    }

    public void DeleteChunksForFile(string repoName, string filePath)
    {
        var keysToRemove = _cache.Keys.Where(k => k.Repo == repoName && k.File == filePath).ToList();
        foreach (var key in keysToRemove)
            _cache.TryRemove(key, out _);
    }

    public void DeleteChunksForRepo(string repoName)
    {
        var keysToRemove = _cache.Keys.Where(k => k.Repo == repoName).ToList();
        foreach (var key in keysToRemove)
            _cache.TryRemove(key, out _);
    }

    public IReadOnlyList<VectorSearchResult> Search(
        float[] queryVector,
        string? repoName = null,
        int limit = 20)
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

    public Dictionary<string, int> GetChunkCounts()
    {
        return _cache.Keys
            .GroupBy(k => k.Repo)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    public bool IsCacheLoaded => _cacheLoaded;
    public int CachedChunkCount => _cache.Count;

    /// <summary>Returns true if the vector cache contains any chunks for the given repo.</summary>
    public bool HasChunksForRepo(string repoName)
        => _cache.Keys.Any(k => k.Repo == repoName);
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
