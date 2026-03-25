namespace HgvMate.Mcp.Configuration;

public class RepoSyncOptions
{
    public const string SectionName = "RepoSync";
    public int PollIntervalMinutes { get; set; } = 15;
    public string ClonePath { get; set; } = "repos";

    /// <summary>
    /// Minimum free disk space (in MB) required before cloning a repository.
    /// Set to 0 to disable the check.
    /// </summary>
    public long MinFreeDiskSpaceMb { get; set; } = 500;
}
