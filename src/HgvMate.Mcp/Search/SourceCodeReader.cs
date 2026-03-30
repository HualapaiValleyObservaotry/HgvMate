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
        => Path.Combine(_syncOptions.ResolveCloneRoot(_hgvMateOptions.DataPath), repoName);

    /// <summary>Maximum file size that can be read (10 MB). Prevents OOM from large auto-generated or binary files.</summary>
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

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

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MaxFileSizeBytes)
            throw new InvalidOperationException(
                $"File '{relativePath}' is too large ({fileInfo.Length / (1024 * 1024)} MB). Maximum allowed size is {MaxFileSizeBytes / (1024 * 1024)} MB.");

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

    /// <summary>
    /// Returns the file tree of a repository up to the given depth using <c>git ls-tree</c>.
    /// Files deeper than <paramref name="depth"/> segments are collapsed to their containing
    /// folder at that depth (shown with a trailing <c>/</c>).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetRepoTreeAsync(
        string repoName,
        string? path = null,
        int depth = 2)
    {
        if (string.IsNullOrWhiteSpace(repoName))
            throw new ArgumentException("Repository name is required.", nameof(repoName));

        var repoRoot = GetRepoRoot(repoName);
        var resolvedRoot = Path.GetFullPath(repoRoot);

        if (!Directory.Exists(Path.Combine(resolvedRoot, ".git")))
            throw new DirectoryNotFoundException($"Repository '{repoName}' is not cloned yet.");

        // Validate and normalize the optional path prefix
        string? normalizedPath = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            normalizedPath = path.Replace('\\', '/').Trim('/');
            var fullCheck = Path.GetFullPath(Path.Combine(resolvedRoot, normalizedPath));
            var relativePath = Path.GetRelativePath(resolvedRoot, fullCheck);
            if (relativePath.StartsWith("..", StringComparison.Ordinal))
                throw new UnauthorizedAccessException($"Path traversal attempt detected: '{path}'");
        }

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = "ls-tree -r --name-only HEAD",
            WorkingDirectory = resolvedRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync());

        var output = stdoutTask.Result;
        var errorOutput = stderrTask.Result;

        if (process.ExitCode != 0)
        {
            _logger.LogError(
                "git ls-tree failed for repository {RepoName} with exit code {ExitCode}. Error: {Error}",
                repoName,
                process.ExitCode,
                errorOutput);

            throw new InvalidOperationException(
                $"git ls-tree failed for repository '{repoName}' with exit code {process.ExitCode}: {errorOutput}");
        }

        var allFiles = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Filter to the requested subtree
        IEnumerable<string> filtered = allFiles;
        if (!string.IsNullOrEmpty(normalizedPath))
        {
            var prefix = normalizedPath + "/";
            filtered = allFiles.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        var prefixLength = string.IsNullOrEmpty(normalizedPath) ? 0 : normalizedPath!.Length + 1;
        var result = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in filtered)
        {
            var relative = file.Substring(prefixLength);
            var segments = relative.Split('/');

            if (segments.Length <= depth)
            {
                // File/path is within the depth limit — include it as-is
                result.Add(string.IsNullOrEmpty(normalizedPath) ? file : $"{normalizedPath}/{relative}");
            }
            else
            {
                // File is deeper — collapse to the folder at the depth boundary
                var collapsed = string.Join("/", segments.Take(depth)) + "/";
                result.Add(string.IsNullOrEmpty(normalizedPath) ? collapsed : $"{normalizedPath}/{collapsed}");
            }
        }

        return result.ToList();
    }

    /// <summary>
    /// Finds files in a repository whose names match a glob <paramref name="pattern"/>.
    /// Patterns without a path separator (e.g. <c>*.csproj</c>, <c>*Controller.cs</c>) are
    /// matched against the file name only; patterns with a separator are matched against the
    /// full relative path.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindFilesAsync(
        string repoName,
        string pattern,
        int maxResults = 200)
    {
        if (string.IsNullOrWhiteSpace(repoName))
            throw new ArgumentException("Repository name is required.", nameof(repoName));
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("Pattern is required.", nameof(pattern));

        var repoRoot = GetRepoRoot(repoName);
        var resolvedRoot = Path.GetFullPath(repoRoot);

        if (!Directory.Exists(Path.Combine(resolvedRoot, ".git")))
            throw new DirectoryNotFoundException($"Repository '{repoName}' is not cloned yet.");

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = "ls-files",
            WorkingDirectory = resolvedRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync());

        var output = stdoutTask.Result;
        var errorOutput = stderrTask.Result;

        if (process.ExitCode != 0)
        {
            _logger.LogError(
                "git ls-files in repository root '{RepoRoot}' failed with exit code {ExitCode}. stderr: {Error}",
                resolvedRoot,
                process.ExitCode,
                errorOutput);

            throw new InvalidOperationException(
                $"git ls-files failed with exit code {process.ExitCode}. See logs for details.");
        }

        var normalizedPattern = pattern.Replace('\\', '/');
        var hasPathSeparator = normalizedPattern.Contains('/');

        var matches = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f =>
            {
                if (hasPathSeparator)
                    return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                        normalizedPattern, f, ignoreCase: true);

                var fileName = f.Contains('/') ? f[(f.LastIndexOf('/') + 1)..] : f;
                return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                    normalizedPattern, fileName, ignoreCase: true);
            })
            .OrderBy(f => f)
            .Take(maxResults)
            .ToList();

        return matches;
    }
}
