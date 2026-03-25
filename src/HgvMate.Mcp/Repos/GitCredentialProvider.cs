using HgvMate.Mcp.Configuration;
using Microsoft.Extensions.Logging;

namespace HgvMate.Mcp.Repos;

public interface IGitCredentialProvider
{
    string? GetToken(string source);
    string BuildAuthenticatedUrl(string url, string source);
}

public class GitCredentialProvider : IGitCredentialProvider
{
    private readonly CredentialOptions _options;
    private readonly ILogger<GitCredentialProvider> _logger;

    public GitCredentialProvider(CredentialOptions options, ILogger<GitCredentialProvider> logger)
    {
        _options = options;
        _logger = logger;
    }

    public string? GetToken(string source)
    {
        return source.ToLowerInvariant() switch
        {
            "github" => _options.GitHubToken ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
            "azuredevops" => _options.AzureDevOpsPat ?? Environment.GetEnvironmentVariable("AZURE_DEVOPS_PAT"),
            _ => null
        };
    }

    public string BuildAuthenticatedUrl(string url, string source)
    {
        var token = GetToken(source);
        if (string.IsNullOrEmpty(token))
            return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        return source.ToLowerInvariant() switch
        {
            "github" => $"https://{token}@{uri.Host}{uri.PathAndQuery}",
            "azuredevops" => BuildAzureDevOpsUrl(uri, token),
            _ => url
        };
    }

    private static string BuildAzureDevOpsUrl(Uri uri, string pat)
    {
        return $"https://:{pat}@{uri.Host}{uri.PathAndQuery}";
    }
}
