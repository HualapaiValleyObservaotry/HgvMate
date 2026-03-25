using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

/// <summary>
/// Live tests that use the real ONNX model (all-MiniLM-L6-v2).
/// These tests are skipped (Inconclusive) if the model file is not available.
/// Run these after provisioning the model via auto-download or Docker build.
/// </summary>
[TestClass]
public sealed class LiveOnnxTests
{
    private OnnxEmbedder? _embedder;

    [TestInitialize]
    public void Setup()
    {
        // Try to find the model in standard locations
        var modelPath = FindModelPath();
        if (modelPath == null)
            return;

        var options = new HgvMateOptions
        {
            DataPath = Path.GetDirectoryName(Path.GetDirectoryName(modelPath))!
        };

        _embedder = new OnnxEmbedder(options, NullLogger<OnnxEmbedder>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _embedder?.Dispose();
    }

    [TestMethod]
    public async Task LiveOnnx_IsAvailable_WhenModelExists()
    {
        if (_embedder == null)
            Assert.Inconclusive("ONNX model not found. Skipping live ONNX test.");

        Assert.IsTrue(_embedder.IsAvailable, "Embedder should be available with real model.");
    }

    [TestMethod]
    public async Task LiveOnnx_EmbedAsync_ReturnsNonZeroVector()
    {
        if (_embedder == null)
            Assert.Inconclusive("ONNX model not found. Skipping live ONNX test.");

        var embedding = await _embedder.EmbedAsync("public class HelloWorld { }");

        Assert.AreEqual(384, embedding.Length, "Embedding should have 384 dimensions.");
        Assert.IsFalse(embedding.All(v => v == 0f), "Embedding should not be all zeros with real model.");
    }

    [TestMethod]
    public async Task LiveOnnx_SimilarTexts_HaveHighSimilarity()
    {
        if (_embedder == null)
            Assert.Inconclusive("ONNX model not found. Skipping live ONNX test.");

        var embedding1 = await _embedder.EmbedAsync("public int Add(int a, int b) => a + b;");
        var embedding2 = await _embedder.EmbedAsync("public int Sum(int x, int y) { return x + y; }");
        var embedding3 = await _embedder.EmbedAsync("SELECT * FROM customers WHERE name LIKE '%test%'");

        var simSimilar = CosineSimilarity(embedding1, embedding2);
        var simDifferent = CosineSimilarity(embedding1, embedding3);

        Assert.IsTrue(simSimilar > simDifferent,
            $"Similar code should have higher similarity ({simSimilar:F4}) than different code ({simDifferent:F4}).");
        Assert.IsTrue(simSimilar > 0.5,
            $"Similar code similarity ({simSimilar:F4}) should be > 0.5.");
    }

    [TestMethod]
    public async Task LiveOnnx_EmbeddingIsNormalized()
    {
        if (_embedder == null)
            Assert.Inconclusive("ONNX model not found. Skipping live ONNX test.");

        var embedding = await _embedder.EmbedAsync("class Program { static void Main() {} }");
        var magnitude = Math.Sqrt(embedding.Sum(x => (double)x * x));

        Assert.IsTrue(Math.Abs(magnitude - 1.0) < 0.01,
            $"Embedding should be unit-normalized. Magnitude: {magnitude:F6}");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private static string? FindModelPath()
    {
        var candidates = new[]
        {
            // In data path (auto-downloaded)
            Path.Combine(AppContext.BaseDirectory, "data", "models", "all-MiniLM-L6-v2.onnx"),
            // Next to app binary (Docker baked)
            Path.Combine(AppContext.BaseDirectory, "models", "all-MiniLM-L6-v2.onnx"),
            // Common dev locations
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hgvmate", "data", "models", "all-MiniLM-L6-v2.onnx"),
            "/app/models/all-MiniLM-L6-v2.onnx",
            "/data/models/all-MiniLM-L6-v2.onnx",
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
