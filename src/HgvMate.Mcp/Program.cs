using HgvMate.Mcp.Api;
using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Data;
using HgvMate.Mcp.Repos;
using HgvMate.Mcp.Search;
using HgvMate.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalar.AspNetCore;

// ── Determine transport before building the host ────────────────────────────
var preConfig = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var transport = Environment.GetEnvironmentVariable("HGVMATE_TRANSPORT")
    ?? preConfig.GetSection("HgvMate")["Transport"]
    ?? "stdio";

var useSse = transport.Equals("sse", StringComparison.OrdinalIgnoreCase)
          || transport.Equals("http", StringComparison.OrdinalIgnoreCase);

if (useSse)
{
    // ── SSE / Streamable HTTP transport ─────────────────────────────────
    var builder = WebApplication.CreateBuilder(args);

    ConfigureServices(builder.Services, builder.Configuration, transport);

    builder.Services.AddOpenApi();

    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithTools<AdminTools>()
        .WithTools<SourceCodeTools>()
        .WithTools<StructuralTools>();

    var app = builder.Build();

    await InitializeDataStores(app.Services);

    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                         | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                         | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost
    });

    app.MapOpenApi();
    app.MapScalarApiReference();
    app.MapMcp("/mcp");
    app.MapRestApi();

    await app.RunAsync();
}
else
{
    // ── stdio transport (default) ───────────────────────────────────────
    var builder = Host.CreateApplicationBuilder(args);

    // Redirect all console logging to stderr so stdout is reserved for MCP JSON-RPC messages
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

    ConfigureServices(builder.Services, builder.Configuration, transport);

    builder.Services.AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<AdminTools>()
        .WithTools<SourceCodeTools>()
        .WithTools<StructuralTools>();

    var app = builder.Build();

    await InitializeDataStores(app.Services);

    await app.RunAsync();
}

// ── Shared service registration ─────────────────────────────────────────────

static void ConfigureServices(IServiceCollection services, IConfiguration configuration, string transport)
{
    var hgvMateOptions = new HgvMateOptions();
    configuration.GetSection(HgvMateOptions.SectionName).Bind(hgvMateOptions);

    var dataPath = Environment.GetEnvironmentVariable("HGVMATE_DATA_PATH") ?? hgvMateOptions.DataPath;
    hgvMateOptions.DataPath = dataPath;
    hgvMateOptions.Transport = transport;

    var repoSyncOptions = new RepoSyncOptions();
    configuration.GetSection(RepoSyncOptions.SectionName).Bind(repoSyncOptions);

    var searchOptions = new SearchOptions();
    configuration.GetSection(SearchOptions.SectionName).Bind(searchOptions);

    var credentialOptions = new CredentialOptions();
    configuration.GetSection(CredentialOptions.SectionName).Bind(credentialOptions);

    Directory.CreateDirectory(dataPath);
    Directory.CreateDirectory(Path.Combine(dataPath, repoSyncOptions.ClonePath));

    var dbPath = Path.Combine(dataPath, "hgvmate.db");
    var connectionString = $"Data Source={dbPath}";

    services.AddSingleton(hgvMateOptions);
    services.AddSingleton(repoSyncOptions);
    services.AddSingleton(searchOptions);
    services.AddSingleton(credentialOptions);

    services.AddSingleton<ISqliteConnectionFactory>(sp =>
        new SqliteConnectionFactory(connectionString, sp.GetRequiredService<ILogger<SqliteConnectionFactory>>()));
    services.AddSingleton<DatabaseInitializer>();
    services.AddSingleton<IGitCredentialProvider, GitCredentialProvider>();

    services.AddSingleton<IRepoRegistry, SqliteRepoRegistry>();
    services.AddSingleton<RepoSyncService>();
    services.AddHostedService<RepoSyncService>(sp => sp.GetRequiredService<RepoSyncService>());

    services.AddSingleton<SourceCodeReader>();
    services.AddSingleton<GitGrepSearchService>();
    services.AddSingleton<GitNexusService>();
    services.AddSingleton<IOnnxEmbedder, OnnxEmbedder>();
    services.AddSingleton<VectorStore>();
    services.AddSingleton<IndexingService>();
    services.AddSingleton<HybridSearchService>();
}

static async Task InitializeDataStores(IServiceProvider services)
{
    var dbInit = services.GetRequiredService<DatabaseInitializer>();
    await dbInit.InitializeAsync();

    var vectorStore = services.GetRequiredService<VectorStore>();
    await vectorStore.EnsureSchemaAsync();
}
