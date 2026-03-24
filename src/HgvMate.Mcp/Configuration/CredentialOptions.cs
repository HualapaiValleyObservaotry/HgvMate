namespace HgvMate.Mcp.Configuration;

public class CredentialOptions
{
    public const string SectionName = "Credentials";
    public string? GitHubToken { get; set; }
    public string? AzureDevOpsPat { get; set; }
}
