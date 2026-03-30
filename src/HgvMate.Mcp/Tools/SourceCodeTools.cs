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
        [Description("Limit search to a specific repository name (optional)")] string? repository = null,
        [Description("Comma-separated file extensions to include, e.g. '.cs,.ts' (optional)")] string? includeExtensions = null,
        [Description("Comma-separated glob patterns to exclude, e.g. '*.min.js,package-lock.json' (optional)")] string? excludePatterns = null)
    {
        HgvMateDiagnostics.RecordToolCall("search_source_code");
        if (string.IsNullOrWhiteSpace(query))
            return "Error: query is required.";

        try
        {
            var extensions = string.IsNullOrWhiteSpace(includeExtensions)
                ? null
                : includeExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var excludes = string.IsNullOrWhiteSpace(excludePatterns)
                ? null
                : excludePatterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var results = await _hybridSearch.SearchAsync(query, repository, extensions, excludes);
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

    [McpServerTool(Name = "hgvmate_get_repo_tree")]
    [Description("List the file/folder structure of a cloned repository up to the specified depth. " +
                 "Folders deeper than the limit are collapsed with a trailing '/'.")]
    public async Task<string> GetRepoTree(
        [Description("Name of the repository")] string repository,
        [Description("Subtree path to list (optional, e.g. 'src/api')")] string? path = null,
        [Description("Maximum folder depth to expand (default: 2, max: 10)")] int depth = 2)
    {
        HgvMateDiagnostics.RecordToolCall("get_repo_tree");
        if (string.IsNullOrWhiteSpace(repository))
            return "Error: repository is required.";
        if (depth < 1 || depth > 10)
            return "Error: depth must be between 1 and 10.";

        try
        {
            var entries = await _reader.GetRepoTreeAsync(repository, path, depth);
            if (!entries.Any())
                return $"No files found{(string.IsNullOrWhiteSpace(path) ? "" : $" under '{path}'")} in repository '{repository}'.";

            var header = string.IsNullOrWhiteSpace(path)
                ? $"File tree for '{repository}' (depth={depth}):"
                : $"File tree for '{repository}/{path}' (depth={depth}):";

            var sb = new StringBuilder();
            sb.AppendLine(header);
            foreach (var entry in entries)
                sb.AppendLine($"  {entry}");
            return sb.ToString();
        }
        catch (DirectoryNotFoundException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error getting repo tree: {ex.Message}";
        }
    }

    [McpServerTool(Name = "hgvmate_find_files")]
    [Description("Search for files by name or glob pattern. " +
                 "Patterns without a path separator (e.g. '*.csproj', '*Controller.cs') match file names only. " +
                 "Patterns with a separator (e.g. 'src/*.ts') match the full relative path.")]
    public async Task<string> FindFiles(
        [Description("Name of the repository")] string repository,
        [Description("Glob pattern to match file names, e.g. '*.cs', '*Controller.cs', 'package.json'")] string pattern)
    {
        HgvMateDiagnostics.RecordToolCall("find_files");
        if (string.IsNullOrWhiteSpace(repository))
            return "Error: repository is required.";
        if (string.IsNullOrWhiteSpace(pattern))
            return "Error: pattern is required.";

        try
        {
            var files = await _reader.FindFilesAsync(repository, pattern);
            if (!files.Any())
                return $"No files matching '{pattern}' found in repository '{repository}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"Files matching '{pattern}' in '{repository}' ({files.Count} result(s)):");
            foreach (var file in files)
                sb.AppendLine($"  {file}");
            return sb.ToString();
        }
        catch (DirectoryNotFoundException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error finding files: {ex.Message}";
        }
    }

    [McpServerTool(Name = "hgvmate_get_techstack")]
    [Description("Get tech stack metadata for a repository from its '.hgvmate/techstack.yml' file. " +
                 "Returns the raw YAML if the file exists. " +
                 "To enable, create '.hgvmate/techstack.yml' in the repository root and commit it.")]
    public async Task<string> GetTechStack(
        [Description("Name of the repository")] string repository)
    {
        HgvMateDiagnostics.RecordToolCall("get_techstack");
        if (string.IsNullOrWhiteSpace(repository))
            return "Error: repository is required.";

        try
        {
            return await _reader.GetFileAsync(repository, ".hgvmate/techstack.yml");
        }
        catch (FileNotFoundException)
        {
            return $"No techstack.yml found for repository '{repository}'. " +
                   "Create '.hgvmate/techstack.yml' in the repo root and commit it to enable this feature.";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error getting techstack: {ex.Message}";
        }
    }
}
