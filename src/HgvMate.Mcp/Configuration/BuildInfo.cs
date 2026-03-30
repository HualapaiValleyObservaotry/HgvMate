using System.Reflection;

namespace HgvMate.Mcp.Configuration;

/// <summary>
/// Reads build-time metadata baked into the assembly by MSBuild.
/// </summary>
public static class BuildInfo
{
    private static readonly Assembly _assembly = Assembly.GetExecutingAssembly();

    public static string Version { get; } =
        _assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? _assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static string GitSha { get; } = GetMetadata("GitSha") ?? "dev";

    public static string BuildDate { get; } = GetMetadata("BuildDate") ?? "unknown";

    private static string? GetMetadata(string key) =>
        _assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;
}
