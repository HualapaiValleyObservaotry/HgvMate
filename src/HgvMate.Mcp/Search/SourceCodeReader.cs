using HgvMate.Mcp.Configuration;
using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp.Search;

public class SourceCodeReader
{
    private readonly HgvMateOptions _hgvMateOptions;
    private readonly RepoSyncOptions _syncOptions;
    private readonly ILogger<SourceCodeReader> _logger;

    public SourceCodeReader(
        HgvMateOptions hgvMateOptions,
        RepoSyncOptions syncOptions,
        ILogger<SourceCodeReader> logger)
    {
        _hgvMateOptions = hgvMateOptions;
        _syncOptions = syncOptions;
        _logger = logger;
    }

    public string GetRepoRoot(string repoName)
    {
        var root = Path.IsPathRooted(_syncOptions.ClonePath)
            ? _syncOptions.ClonePath
            : Path.Combine(_hgvMateOptions.DataPath, _syncOptions.ClonePath);
        return Path.Combine(root, repoName);
    }

    /// <summary>
    /// Reads a file from a cloned repository, with path traversal protection.
    /// </summary>
    public async Task<string> GetFileAsync(string repoName, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(repoName))
            throw new ArgumentException("Repository name is required.", nameof(repoName));
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("File path is required.", nameof(relativePath));

        var repoRoot = GetRepoRoot(repoName);
        var resolvedRoot = Path.GetFullPath(repoRoot);

        // Normalize the relative path - replace backslashes
        var normalizedRelative = relativePath.Replace('\\', '/').TrimStart('/');

        var fullPath = Path.GetFullPath(Path.Combine(resolvedRoot, normalizedRelative));

        // Path traversal protection
        if (!fullPath.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !fullPath.Equals(resolvedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Path traversal attempt detected: '{relativePath}'");
        }

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: '{relativePath}' in repository '{repoName}'.");

        return await File.ReadAllTextAsync(fullPath);
    }

    /// <summary>
    /// Lists files/directories in a repo directory, with path traversal protection.
    /// </summary>
    public Task<IReadOnlyList<string>> ListDirectoryAsync(string repoName, string relativePath = "")
    {
        if (string.IsNullOrWhiteSpace(repoName))
            throw new ArgumentException("Repository name is required.", nameof(repoName));

        var repoRoot = GetRepoRoot(repoName);
        var resolvedRoot = Path.GetFullPath(repoRoot);

        var normalizedRelative = (relativePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
        var targetPath = string.IsNullOrEmpty(normalizedRelative)
            ? resolvedRoot
            : Path.GetFullPath(Path.Combine(resolvedRoot, normalizedRelative));

        // Path traversal protection
        if (!targetPath.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Path traversal attempt detected: '{relativePath}'");

        if (!Directory.Exists(targetPath))
            throw new DirectoryNotFoundException($"Directory not found: '{relativePath}' in repository '{repoName}'.");

        var entries = Directory.EnumerateFileSystemEntries(targetPath)
            .Select(e => Path.GetRelativePath(resolvedRoot, e))
            .OrderBy(e => e)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(entries);
    }
}
