using HgvMate.Mcp.Configuration;
using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp.Search;

public record SearchResult(string RepoName, string FilePath, int LineNumber, string LineContent, float Score = 1.0f);

public class HybridSearchService
{
    private readonly GitGrepSearchService _grepService;
    private readonly VectorStore _vectorStore;
    private readonly IOnnxEmbedder _embedder;
    private readonly SearchOptions _searchOptions;
    private readonly ILogger<HybridSearchService> _logger;

    public HybridSearchService(
        GitGrepSearchService grepService,
        VectorStore vectorStore,
        IOnnxEmbedder embedder,
        SearchOptions searchOptions,
        ILogger<HybridSearchService> logger)
    {
        _grepService = grepService;
        _vectorStore = vectorStore;
        _embedder = embedder;
        _searchOptions = searchOptions;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        string? repositoryName = null,
        CancellationToken cancellationToken = default)
    {
        var grepTask = _grepService.SearchAsync(query, repositoryName, cancellationToken);
        var vectorTask = SearchVectorAsync(query, repositoryName, cancellationToken);

        await Task.WhenAll(grepTask, vectorTask);

        var results = new List<SearchResult>();
        foreach (var r in grepTask.Result)
            results.Add(new SearchResult(r.RepoName, r.FilePath, r.LineNumber, r.LineContent, 1.0f));

        var existingFiles = results.Select(r => (r.RepoName, r.FilePath)).ToHashSet();
        foreach (var r in vectorTask.Result)
        {
            if (!existingFiles.Contains((r.RepoName, r.FilePath)))
                results.Add(new SearchResult(r.RepoName, r.FilePath, 0, r.Content, r.Score));
        }

        return results
            .OrderByDescending(r => r.Score)
            .Take(_searchOptions.MaxResults)
            .ToList();
    }

    private async Task<IReadOnlyList<VectorSearchResult>> SearchVectorAsync(
        string query,
        string? repositoryName,
        CancellationToken cancellationToken)
    {
        if (!_embedder.IsAvailable)
            return [];

        try
        {
            var queryVector = await _embedder.EmbedAsync(query, cancellationToken);
            return await _vectorStore.SearchAsync(queryVector, repositoryName, _searchOptions.MaxResults);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vector search failed; falling back to text search only.");
            return [];
        }
    }
}
