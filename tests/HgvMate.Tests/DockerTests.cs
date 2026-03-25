using System.Diagnostics;
using System.Text.Json;

namespace HgvMate.Tests;

/// <summary>
/// Docker-specific tests. These verify:
/// 1. The Docker image builds successfully
/// 2. The container starts and responds to MCP protocol
/// 3. The ONNX model is baked into the image
///
/// These tests require Docker to be available and may take minutes to run.
/// They are skipped (Inconclusive) if Docker is not available.
/// </summary>
[TestClass]
public sealed class DockerTests
{
    private const string ImageName = "hgvmate-test";
    private const string DockerfilePath = "Dockerfile";
    private static bool _imageBuilt;
    private static readonly object _buildLock = new();

    [TestMethod]
    public void Docker_BuildSucceeds()
    {
        if (!IsDockerAvailable())
            Assert.Inconclusive("Docker is not available.");

        var contextDir = FindRepoRoot();
        if (contextDir == null)
            Assert.Inconclusive("Cannot find repository root with Dockerfile.");

        EnsureImageBuilt(contextDir);
    }

    [TestMethod]
    public async Task Docker_ContainerStartsAndRespondsToInitialize()
    {
        if (!IsDockerAvailable())
            Assert.Inconclusive("Docker is not available.");

        var contextDir = FindRepoRoot();
        if (contextDir == null)
            Assert.Inconclusive("Cannot find repository root with Dockerfile.");

        EnsureImageBuilt(contextDir);

        // Run container in interactive mode (stdio transport)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"run --rm -i {ImageName}",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        try
        {
            // Wait briefly for container startup
            await Task.Delay(2000, cts.Token);

            // Send MCP initialize
            var initRequest = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-03-26",
                    capabilities = new { },
                    clientInfo = new { name = "docker-test", version = "1.0.0" }
                }
            });

            await process.StandardInput.WriteLineAsync(initRequest.AsMemory(), cts.Token);
            await process.StandardInput.FlushAsync(cts.Token);

            // Read response
            var line = await process.StandardOutput.ReadLineAsync(cts.Token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(30), cts.Token);

            Assert.IsNotNull(line, "Container should respond to MCP initialize.");

            var doc = JsonDocument.Parse(line);
            Assert.IsTrue(doc.RootElement.TryGetProperty("result", out var result),
                $"Response should have 'result'. Got: {line}");
            Assert.IsTrue(result.TryGetProperty("serverInfo", out _),
                "Result should contain serverInfo.");
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(10_000))
                    process.Kill(entireProcessTree: true);
            }
            catch { }
        }
    }

    [TestMethod]
    public void Docker_ImageContainsOnnxModel()
    {
        if (!IsDockerAvailable())
            Assert.Inconclusive("Docker is not available.");

        var contextDir = FindRepoRoot();
        if (contextDir == null)
            Assert.Inconclusive("Cannot find repository root with Dockerfile.");

        EnsureImageBuilt(contextDir);

        // Check if the ONNX model file exists in the image
        var result = RunCommand("docker",
            $"run --rm --entrypoint /bin/sh {ImageName} -c \"test -f /app/models/all-MiniLM-L6-v2.onnx && echo EXISTS || echo MISSING\"",
            timeoutMs: 30_000);

        Assert.IsTrue(result.output.Trim().Contains("EXISTS"),
            $"ONNX model should be baked into the Docker image at /app/models/. Got: {result.output}");
    }

    [TestMethod]
    public void Docker_ImageContainsGit()
    {
        if (!IsDockerAvailable())
            Assert.Inconclusive("Docker is not available.");

        var contextDir = FindRepoRoot();
        if (contextDir == null)
            Assert.Inconclusive("Cannot find repository root with Dockerfile.");

        EnsureImageBuilt(contextDir);

        var result = RunCommand("docker",
            $"run --rm --entrypoint git {ImageName} --version",
            timeoutMs: 30_000);

        Assert.AreEqual(0, result.exitCode, $"git should be available in container. Stderr: {result.stderr}");
        Assert.IsTrue(result.output.Contains("git version"), $"Expected 'git version', got: {result.output}");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static void EnsureImageBuilt(string contextDir)
    {
        lock (_buildLock)
        {
            if (_imageBuilt) return;

            var result = RunCommand("docker",
                $"build -t {ImageName} -f {Path.Combine(contextDir, DockerfilePath)} {contextDir}",
                timeoutMs: 600_000); // 10 min timeout for build

            Assert.AreEqual(0, result.exitCode,
                $"Docker build failed with exit code {result.exitCode}.\nStdout:\n{result.output}\nStderr:\n{result.stderr}");

            _imageBuilt = true;
        }
    }

    private static bool IsDockerAvailable()
    {
        try
        {
            var result = RunCommand("docker", "version", timeoutMs: 10_000);
            return result.exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (string output, string stderr, int exitCode) RunCommand(
        string fileName, string arguments, int timeoutMs)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Command '{fileName} {arguments}' timed out after {timeoutMs}ms.");
        }

        return (stdout, stderr, process.ExitCode);
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, DockerfilePath))
                && File.Exists(Path.Combine(dir, "HgvMate.slnx")))
            {
                return dir;
            }
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        return null;
    }
}
