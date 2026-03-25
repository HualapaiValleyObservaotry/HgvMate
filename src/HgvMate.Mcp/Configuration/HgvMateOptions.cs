namespace HgvMate.Mcp.Configuration;

public class HgvMateOptions
{
    public const string SectionName = "HgvMate";
    public string DataPath { get; set; } = "./data";
    public string Transport { get; set; } = "stdio";
}
