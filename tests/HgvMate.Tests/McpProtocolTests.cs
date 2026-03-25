using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HgvMate.Tests;

/// <summary>
/// Live MCP protocol tests. These spawn the actual HgvMate.Mcp process and
/// communicate via stdio using JSON-RPC, verifying the real MCP wire protocol.
/// Skips gracefully if the binary cannot be built/found.
/// </summary>
[TestClass]
public sealed class McpProtocolTests
{
    private string _tempDir = null!;
    private string? _executablePath;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "HgvMateMcp_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);

        // Try to locate the built executable
        var binPath = Path.Combine(AppContext.BaseDirectory, "HgvMate.Mcp");
        if (File.Exists(binPath))
        {
            _executablePath = binPath;
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task McpStdio_Initialize_ReturnsServerInfo()
    {
        if (_executablePath == null)
            Assert.Inconclusive("HgvMate.Mcp executable not found. Run 'dotnet build' first.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = StartMcpProcess();

        try
        {
            // Send MCP initialize request
            var initRequest = CreateJsonRpcRequest(1, "initialize", new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "test-client", version = "1.0.0" }
            });

            await SendMessageAsync(process, initRequest, cts.Token);

            var response = await ReadResponseAsync(process, cts.Token);
            Assert.IsNotNull(response, "Should receive an initialize response.");

            // Verify it's a valid JSON-RPC response with id=1
            var resp = response.Value;
            Assert.IsTrue(resp.TryGetProperty("jsonrpc", out var jsonrpc));
            Assert.AreEqual("2.0", jsonrpc.GetString());
            Assert.IsTrue(resp.TryGetProperty("id", out var id));
            Assert.AreEqual(1, id.GetInt32());

            // Verify result contains serverInfo
            Assert.IsTrue(resp.TryGetProperty("result", out var result), "Response should have a 'result' field.");
            Assert.IsTrue(result.TryGetProperty("serverInfo", out _), "Result should contain 'serverInfo'.");
        }
        finally
        {
            await StopProcessAsync(process);
        }
    }

    [TestMethod]
    public async Task McpStdio_ToolsList_ContainsHgvmateTools()
    {
        if (_executablePath == null)
            Assert.Inconclusive("HgvMate.Mcp executable not found. Run 'dotnet build' first.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = StartMcpProcess();

        try
        {
            // 1. Initialize
            await SendMessageAsync(process, CreateJsonRpcRequest(1, "initialize", new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "test-client", version = "1.0.0" }
            }), cts.Token);
            await ReadResponseAsync(process, cts.Token);

            // 2. Send initialized notification
            await SendMessageAsync(process, CreateJsonRpcNotification("notifications/initialized"), cts.Token);

            // 3. List tools
            await SendMessageAsync(process, CreateJsonRpcRequest(2, "tools/list", new { }), cts.Token);
            var toolsResponse = await ReadResponseAsync(process, cts.Token);

            Assert.IsNotNull(toolsResponse, "Should receive tools/list response.");
            Assert.IsTrue(toolsResponse.Value.TryGetProperty("result", out var result));
            Assert.IsTrue(result.TryGetProperty("tools", out var tools));

            var toolNames = new List<string>();
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.TryGetProperty("name", out var name))
                    toolNames.Add(name.GetString()!);
            }

            // Verify core hgvmate tools are present
            Assert.Contains("hgvmate_list_repositories", toolNames,
                "Should contain hgvmate_list_repositories tool.");
            Assert.Contains("hgvmate_search_source_code", toolNames,
                "Should contain hgvmate_search_source_code tool.");
            Assert.Contains("hgvmate_find_symbol", toolNames,
                "Should contain hgvmate_find_symbol tool.");

            // Verify all 11 expected tools
            Assert.IsGreaterThanOrEqualTo(11, toolNames.Count,
                $"Expected at least 11 hgvmate tools, found {toolNames.Count}: {string.Join(", ", toolNames)}");
        }
        finally
        {
            await StopProcessAsync(process);
        }
    }

    [TestMethod]
    public async Task McpStdio_CallTool_ListRepositories_ReturnsResult()
    {
        if (_executablePath == null)
            Assert.Inconclusive("HgvMate.Mcp executable not found. Run 'dotnet build' first.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = StartMcpProcess();

        try
        {
            // 1. Initialize
            await SendMessageAsync(process, CreateJsonRpcRequest(1, "initialize", new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "test-client", version = "1.0.0" }
            }), cts.Token);
            await ReadResponseAsync(process, cts.Token);

            // 2. Initialized notification
            await SendMessageAsync(process, CreateJsonRpcNotification("notifications/initialized"), cts.Token);

            // 3. Call hgvmate_list_repositories
            await SendMessageAsync(process, CreateJsonRpcRequest(3, "tools/call", new
            {
                name = "hgvmate_list_repositories",
                arguments = new { }
            }), cts.Token);

            var callResponse = await ReadResponseAsync(process, cts.Token);
            Assert.IsNotNull(callResponse, "Should receive tools/call response.");
            Assert.IsTrue(callResponse.Value.TryGetProperty("result", out var result),
                "Response should have a 'result' field (not an error).");
        }
        finally
        {
            await StopProcessAsync(process);
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private Process StartMcpProcess()
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executablePath!,
                WorkingDirectory = _tempDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Environment =
                {
                    ["HGVMATE_DATA_PATH"] = _tempDir,
                    ["HGVMATE_TRANSPORT"] = "stdio",
                    ["DOTNET_ENVIRONMENT"] = "Testing",
                    ["RepoSync__PollIntervalMinutes"] = "0"
                }
            }
        };

        process.Start();
        return process;
    }

    private static string CreateJsonRpcRequest(int id, string method, object? @params)
    {
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params
        });
    }

    private static string CreateJsonRpcNotification(string method)
    {
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method
        });
    }

    private static async Task SendMessageAsync(Process process, string message, CancellationToken ct)
    {
        await process.StandardInput.WriteLineAsync(message.AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
    }

    private static async Task<JsonElement?> ReadResponseAsync(Process process, CancellationToken ct)
    {
        // Read lines until we find a valid JSON-RPC response (skip log output on stderr)
        while (!ct.IsCancellationRequested)
        {
            var readTask = process.StandardOutput.ReadLineAsync(ct);
            var line = await readTask.AsTask().WaitAsync(TimeSpan.FromSeconds(15), ct);

            if (line == null) return null; // EOF

            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            try
            {
                var doc = JsonDocument.Parse(line);
                // Only return JSON-RPC responses (have "id" field), skip notifications
                if (doc.RootElement.TryGetProperty("id", out _))
                    return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Not JSON — likely a log line that leaked to stdout, skip it
            }
        }

        return null;
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(5000))
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }
}
