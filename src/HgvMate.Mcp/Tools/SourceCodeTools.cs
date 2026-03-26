using HgvMate.Mcp.Search;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace HgvMate.Mcp.Tools;

[McpServerToolType]
public class SourceCodeTools
{
    private readonly HybridSearchService _hybridSearch;
    private readonly SourceCodeReader _reader;

    public SourceCodeTools(HybridSearchService hybridSearch, SourceCodeReader reader)
    {
        _hybridSearch = hybridSearch;
        _reader = reader;
    }

    [McpServerTool(Name = "hgvmate_search_source_code")]
    [Description("Search source code using text and semantic search across indexed repositories.")]
    public async Task<string> SearchSourceCode(
        [Description("Search query — keywords, function names, or natural language description")] string query,
        [Description("Limit search to a specific repository name (optional)")] string? repository = null)
    {
        HgvMateDiagnostics.RecordToolCall("search_source_code");
        if (string.IsNullOrWhiteSpace(query))
            return "Error: query is required.";

        try
        {
            var results = await _hybridSearch.SearchAsync(query, repository);
            if (!results.Any())
                return $"No results found for query: '{query}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"Search results for '{query}':");
            foreach (var result in results)
            {
                sb.AppendLine($"\n[{result.RepoName}] {result.FilePath}:{result.LineNumber}");
                sb.AppendLine($"  {result.LineContent.Trim()}");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error searching source code: {ex.Message}";
        }
    }

    [McpServerTool(Name = "hgvmate_get_file_content")]
    [Description("Read the content of a source file from a cloned repository.")]
    public async Task<string> GetFileContent(
        [Description("Name of the repository")] string repository,
        [Description("Relative path to the file within the repository (e.g., 'src/Program.cs')")] string path)
    {
        HgvMateDiagnostics.RecordToolCall("get_file_content");
        if (string.IsNullOrWhiteSpace(repository))
            return "Error: repository is required.";
        if (string.IsNullOrWhiteSpace(path))
            return "Error: path is required.";

        try
        {
            return await _reader.GetFileAsync(repository, path);
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (FileNotFoundException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }
}
