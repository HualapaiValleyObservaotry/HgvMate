using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Data;
using HgvMate.Mcp.Repos;
using HgvMate.Mcp.Search;
using HgvMate.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Configuration
var hgvMateOptions = new HgvMateOptions();
builder.Configuration.GetSection(HgvMateOptions.SectionName).Bind(hgvMateOptions);

var dataPath = Environment.GetEnvironmentVariable("HGVMATE_DATA_PATH") ?? hgvMateOptions.DataPath;
hgvMateOptions.DataPath = dataPath;

var transport = Environment.GetEnvironmentVariable("HGVMATE_TRANSPORT") ?? hgvMateOptions.Transport;
hgvMateOptions.Transport = transport;

var repoSyncOptions = new RepoSyncOptions();
builder.Configuration.GetSection(RepoSyncOptions.SectionName).Bind(repoSyncOptions);

var searchOptions = new SearchOptions();
builder.Configuration.GetSection(SearchOptions.SectionName).Bind(searchOptions);

var credentialOptions = new CredentialOptions();
builder.Configuration.GetSection(CredentialOptions.SectionName).Bind(credentialOptions);

Directory.CreateDirectory(dataPath);
Directory.CreateDirectory(Path.Combine(dataPath, repoSyncOptions.ClonePath));

var dbPath = Path.Combine(dataPath, "hgvmate.db");
var connectionString = $"Data Source={dbPath}";

builder.Services.AddSingleton(hgvMateOptions);
builder.Services.AddSingleton(repoSyncOptions);
builder.Services.AddSingleton(searchOptions);
builder.Services.AddSingleton(credentialOptions);

builder.Services.AddSingleton<ISqliteConnectionFactory>(sp =>
    new SqliteConnectionFactory(connectionString, sp.GetRequiredService<ILogger<SqliteConnectionFactory>>()));
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<IGitCredentialProvider, GitCredentialProvider>();

builder.Services.AddSingleton<IRepoRegistry, SqliteRepoRegistry>();
builder.Services.AddSingleton<RepoSyncService>();
builder.Services.AddHostedService<RepoSyncService>(sp => sp.GetRequiredService<RepoSyncService>());

builder.Services.AddSingleton<SourceCodeReader>();
builder.Services.AddSingleton<GitGrepSearchService>();
builder.Services.AddSingleton<GitNexusService>();
builder.Services.AddSingleton<IOnnxEmbedder, OnnxEmbedder>();
builder.Services.AddSingleton<VectorStore>();
builder.Services.AddSingleton<IndexingService>();
builder.Services.AddSingleton<HybridSearchService>();

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<AdminTools>()
    .WithTools<SourceCodeTools>()
    .WithTools<StructuralTools>();

var app = builder.Build();

var dbInit = app.Services.GetRequiredService<DatabaseInitializer>();
await dbInit.InitializeAsync();

var vectorStore = app.Services.GetRequiredService<VectorStore>();
await vectorStore.EnsureSchemaAsync();

await app.RunAsync();
