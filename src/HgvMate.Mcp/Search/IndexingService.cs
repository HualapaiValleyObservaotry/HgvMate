using HgvMate.Mcp.Configuration;
using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp.Search;

public class IndexingService
{
    private static readonly string[] TextExtensions =
    [
        ".cs", ".ts", ".js", ".tsx", ".jsx", ".py", ".java", ".go", ".rs",
        ".cpp", ".c", ".h", ".hpp", ".rb", ".php", ".swift", ".kt", ".scala",
        ".md", ".txt", ".json", ".xml", ".yaml", ".yml", ".toml", ".sh", ".sql"
    ];

    private readonly VectorStore _vectorStore;
    private readonly IOnnxEmbedder _embedder;
    private readonly SourceCodeReader _reader;
    private readonly SearchOptions _searchOptions;
    private readonly ILogger<IndexingService> _logger;

    public IndexingService(
        VectorStore vectorStore,
        IOnnxEmbedder embedder,
        SourceCodeReader reader,
        SearchOptions searchOptions,
        ILogger<IndexingService> logger)
    {
        _vectorStore = vectorStore;
        _embedder = embedder;
        _reader = reader;
        _searchOptions = searchOptions;
        _logger = logger;
    }

    public virtual async Task IndexRepoAsync(string repoName, CancellationToken cancellationToken = default)
    {
        if (!_embedder.IsAvailable)
        {
            _logger.LogInformation("Skipping vector indexing for '{Repo}': ONNX model not available.", repoName);
            return;
        }

        _logger.LogInformation("Indexing repo '{Repo}' for vector search...", repoName);
        var repoRoot = _reader.GetRepoRoot(repoName);

        if (!Directory.Exists(repoRoot))
        {
            _logger.LogWarning("Repo '{Repo}' not cloned yet.", repoName);
            return;
        }

        await _vectorStore.DeleteChunksForRepoAsync(repoName);

        var files = GetIndexableFiles(repoRoot);
        int fileCount = 0, chunkCount = 0;

        foreach (var filePath in files)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                var relativePath = Path.GetRelativePath(repoRoot, filePath);
                var content = await File.ReadAllTextAsync(filePath, cancellationToken);
                var chunks = ChunkText(content);
                var sourceChunks = new List<SourceChunk>();

                for (int i = 0; i < chunks.Count; i++)
                {
                    var embedding = await _embedder.EmbedAsync(chunks[i], cancellationToken);
                    sourceChunks.Add(new SourceChunk(repoName, relativePath, i, chunks[i], embedding));
                    chunkCount++;
                }

                await _vectorStore.UpsertChunksAsync(sourceChunks);
                fileCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index file '{File}'.", filePath);
            }
        }

        _logger.LogInformation("Indexed {Files} files ({Chunks} chunks) for repo '{Repo}'.", fileCount, chunkCount, repoName);
    }

    public virtual async Task IndexFileAsync(string repoName, string relativePath, CancellationToken cancellationToken = default)
    {
        if (!_embedder.IsAvailable) return;

        var repoRoot = _reader.GetRepoRoot(repoName);
        var fullPath = Path.Combine(repoRoot, relativePath);

        if (!File.Exists(fullPath)) return;

        await _vectorStore.DeleteChunksForFileAsync(repoName, relativePath);
        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var chunks = ChunkText(content);
        var sourceChunks = new List<SourceChunk>();

        for (int i = 0; i < chunks.Count; i++)
        {
            var embedding = await _embedder.EmbedAsync(chunks[i], cancellationToken);
            sourceChunks.Add(new SourceChunk(repoName, relativePath, i, chunks[i], embedding));
        }

        await _vectorStore.UpsertChunksAsync(sourceChunks);
    }

    private IReadOnlyList<string> ChunkText(string text)
    {
        var lines = text.Split('\n');
        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();
        int tokenEstimate = 0;

        foreach (var line in lines)
        {
            var lineTokens = line.Length / 4 + 1; // rough estimate: 4 chars per token
            if (tokenEstimate + lineTokens > _searchOptions.ChunkSize && current.Length > 0)
            {
                chunks.Add(current.ToString().Trim());
                // Retain trailing overlap window for context continuity
                var currentText = current.ToString();
                var overlapChars = _searchOptions.ChunkOverlap * 4;
                current.Clear();
                if (currentText.Length > overlapChars)
                    current.Append(currentText[^overlapChars..]);
                else
                    current.Append(currentText);
                tokenEstimate = current.Length / 4;
            }
            current.AppendLine(line);
            tokenEstimate += lineTokens;
        }

        if (current.Length > 0)
            chunks.Add(current.ToString().Trim());

        return chunks.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
    }

    private static IEnumerable<string> GetIndexableFiles(string root)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => TextExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".gitnexus" + Path.DirectorySeparatorChar))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));
    }
}
