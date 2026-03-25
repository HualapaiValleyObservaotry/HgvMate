namespace HgvMate.Mcp.Configuration;

public class RepoSyncOptions
{
    public const string SectionName = "RepoSync";
    public int PollIntervalMinutes { get; set; } = 15;
    public string ClonePath { get; set; } = "repos";
}
