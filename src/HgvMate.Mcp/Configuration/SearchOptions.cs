namespace HgvMate.Mcp.Configuration;

public class SearchOptions
{
    public const string SectionName = "Search";
    public int MaxResults { get; set; } = 20;
    public int ChunkSize { get; set; } = 800;
    public int ChunkOverlap { get; set; } = 100;
}
