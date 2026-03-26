namespace HgvMate.Mcp.Configuration;

public class RepoSyncOptions
{
    public const string SectionName = "RepoSync";
    public int PollIntervalMinutes { get; set; } = 15;
    /// <summary>
    /// Path where repositories are cloned.
    /// If absolute (starts with /), used as-is (ephemeral local storage).
    /// If relative, resolved under HgvMateOptions.DataPath (persistent storage).
    /// </summary>
    public string ClonePath { get; set; } = "repos";

    /// <summary>
    /// Minimum free disk space (in MB) required before cloning a repository.
    /// Set to 0 to disable the check.
    /// </summary>
    public long MinFreeDiskSpaceMb { get; set; } = 1024;
}
