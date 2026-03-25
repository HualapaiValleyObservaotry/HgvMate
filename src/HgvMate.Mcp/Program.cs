using HgvMate.Mcp;
using HgvMate.Mcp.Api;
using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Data;
using HgvMate.Mcp.Repos;
using HgvMate.Mcp.Search;
using HgvMate.Mcp.Tools;
using HVO.Enterprise.Telemetry;
using HVO.Enterprise.Telemetry.Abstractions;
using HVO.Enterprise.Telemetry.Correlation;
using HVO.Enterprise.Telemetry.Data.AdoNet;
using HVO.Enterprise.Telemetry.Data.AdoNet.Extensions;
using HVO.Enterprise.Telemetry.HealthChecks;
using HVO.Enterprise.Telemetry.Logging;
using HVO.Enterprise.Telemetry.OpenTelemetry;
using Microsoft.AspNetCore.DataProtection;
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

    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                         | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                         | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost
    });

    // Correlation ID middleware — assigns/propagates X-Correlation-ID (before exception handler)
    app.Use(async (context, next) =>
    {
        var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault()
            ?? Guid.NewGuid().ToString("N");
        using var scope = CorrelationContext.BeginScope(correlationId);
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        await next();
    });

    app.UseExceptionHandler(errApp =>
    {
        errApp.Run(async context =>
        {
            var ex = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            var logger = context.RequestServices.GetRequiredService<ILogger<WebApplication>>();
            var traceId = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;

            logger.LogError(ex, "Unhandled exception on {Method} {Path} [traceId={TraceId}]",
                context.Request.Method, context.Request.Path, traceId);

            bool isBusy = ex is Microsoft.Data.Sqlite.SqliteException sqlex &&
                          (sqlex.SqliteErrorCode == 5 || sqlex.SqliteErrorCode == 6); // SQLITE_BUSY / SQLITE_LOCKED

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = isBusy ? 503 : 500;
            if (isBusy)
                context.Response.Headers["Retry-After"] = "5";

            var isDev = app.Environment.IsDevelopment();
            await context.Response.WriteAsJsonAsync(new
            {
                error = isBusy ? "Service temporarily unavailable." : "An unexpected error occurred.",
                detail = isBusy || isDev ? ex?.Message : null,
                traceId
            });
        });
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

    var keysDir = new DirectoryInfo(Path.Combine(dataPath, "DataProtection-Keys"));
    services.AddDataProtection()
        .PersistKeysToFileSystem(keysDir);

    var dbPath = Path.Combine(dataPath, "hgvmate.db");
    var connectionString = $"Data Source={dbPath};Default Timeout=30";

    services.AddSingleton(hgvMateOptions);
    services.AddSingleton(repoSyncOptions);
    services.AddSingleton(searchOptions);
    services.AddSingleton(credentialOptions);

    // ── HVO.Enterprise.Telemetry ────────────────────────────────────────
    services.AddTelemetry(configuration.GetSection("Telemetry"));

    services.AddTelemetryLoggingEnrichment(options =>
    {
        options.IncludeCorrelationId = true;
        options.IncludeTraceId = true;
        options.IncludeSpanId = true;
    });

    services.AddAdoNetTelemetry(options =>
    {
        options.RecordStatements = true;
        options.RecordParameters = false; // never log parameter values (PII safety)
        options.RecordConnectionInfo = false;
    });

    var telemetrySection = configuration.GetSection("Telemetry");
    var serviceName = telemetrySection["ServiceName"] ?? "HgvMate";
    var serviceVersion = telemetrySection["ServiceVersion"] ?? "1.0.0";
    // Support Aspire / Azure Container Apps: OTEL_EXPORTER_OTLP_ENDPOINT is injected automatically
    var otlpEndpoint = telemetrySection["OtlpEndpoint"]
        ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
    if (!string.IsNullOrEmpty(otlpEndpoint))
    {
        services.AddOpenTelemetryExport(options =>
        {
            options.ServiceName = serviceName;
            options.ServiceVersion = serviceVersion;
            options.Endpoint = otlpEndpoint;
            options.EnableTraceExport = true;
            options.EnableMetricsExport = true;
            options.EnableLogExport = true;
        });
    }

    services.AddTelemetryStatistics();
    services.AddTelemetryHealthCheck(new TelemetryHealthCheckOptions
    {
        DegradedErrorRateThreshold = 5.0,
        UnhealthyErrorRateThreshold = 20.0,
        MaxExpectedQueueDepth = 10000,
    });

    // ── Data + Services ─────────────────────────────────────────────────
    services.AddSingleton<ISqliteConnectionFactory>(sp =>
        new SqliteConnectionFactory(connectionString, sp.GetRequiredService<ILogger<SqliteConnectionFactory>>()));
    services.AddSingleton<DatabaseInitializer>();
    services.AddSingleton<IGitCredentialProvider, GitCredentialProvider>();

    services.AddSingleton<StartupState>();
    services.AddHostedService<WarmupService>();

    services.AddSingleton<IRepoRegistry, SqliteRepoRegistry>();
    services.AddSingleton<RepoSyncService>();
    services.AddHostedService<RepoSyncService>(sp => sp.GetRequiredService<RepoSyncService>());

    services.AddSingleton<SourceCodeReader>();
    services.AddSingleton<GitGrepSearchService>();
    services.AddSingleton<GitNexusService>();
    services.AddSingleton<IOnnxEmbedder, OnnxEmbedder>();
    services.AddSingleton(sp =>
        new VectorStore(
            Path.Combine(dataPath, "vectors.bin"),
            sp.GetRequiredService<ILogger<VectorStore>>()));
    services.AddSingleton<IndexingService>();
    services.AddSingleton<HybridSearchService>();
}


