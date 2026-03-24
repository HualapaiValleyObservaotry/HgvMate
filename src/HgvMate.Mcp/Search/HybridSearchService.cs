using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp.Search;

public record SearchResult(string RepoName, string FilePath, int LineNumber, string LineContent, float Score = 1.0f);

public class HybridSearchService
{
    private readonly GitGrepSearchService _grepService;
    private readonly ILogger<HybridSearchService> _logger;

    public HybridSearchService(GitGrepSearchService grepService, ILogger<HybridSearchService> logger)
    {
        _grepService = grepService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        string? repositoryName = null,
        CancellationToken cancellationToken = default)
    {
        var grepResults = await _grepService.SearchAsync(query, repositoryName, cancellationToken);
        return grepResults
            .Select(r => new SearchResult(r.RepoName, r.FilePath, r.LineNumber, r.LineContent))
            .ToList();
    }
}
