using HgvMate.Mcp.Configuration;
using HgvMate.Mcp.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace HgvMate.Tests;

[TestClass]
public sealed class OnnxEmbedderTests
{
    [TestMethod]
    public void OnnxEmbedder_Dimensions_Is384()
    {
        var embedder = new OnnxEmbedder((Microsoft.ML.OnnxRuntime.InferenceSession?)null,
            NullLogger<OnnxEmbedder>.Instance);
        Assert.AreEqual(384, embedder.Dimensions);
    }

    [TestMethod]
    public void OnnxEmbedder_WithoutModel_IsNotAvailable()
    {
        var embedder = new OnnxEmbedder((Microsoft.ML.OnnxRuntime.InferenceSession?)null,
            NullLogger<OnnxEmbedder>.Instance);
        Assert.IsFalse(embedder.IsAvailable);
    }

    [TestMethod]
    public async Task EmbedAsync_WithoutModel_ReturnsZeroVector()
    {
        var embedder = new OnnxEmbedder((Microsoft.ML.OnnxRuntime.InferenceSession?)null,
            NullLogger<OnnxEmbedder>.Instance);
        var result = await embedder.EmbedAsync("hello world");
        Assert.HasCount(384, result);
        Assert.IsTrue(result.All(static v => v == 0f));
    }

    // ─── Tokenizer regression tests ──────────────────────────────────────────

    [TestMethod]
    public void SimpleTokenize_AllTokenIds_WithinVocabRange()
    {
        // Regression: tokenizer was generating IDs > 30521 (vocab size = 30,522)
        var inputs = new[]
        {
            "public class OnnxEmbedder : IOnnxEmbedder, IDisposable",
            "hello world foo bar baz",
            "Microsoft.ML.OnnxRuntime.InferenceSession",
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789",
            string.Join(" ", Enumerable.Range(0, 200).Select(i => $"word{i}"))
        };

        foreach (var input in inputs)
        {
            var tokens = OnnxEmbedder.SimpleTokenize(input);
            foreach (var token in tokens)
            {
                Assert.IsTrue(token >= 0 && token < 30522,
                    $"Token ID {token} is outside valid range [0, 30521] for input: {input}");
            }
        }
    }

    [TestMethod]
    public void SimpleTokenize_IncludesClsAndSepTokens()
    {
        var tokens = OnnxEmbedder.SimpleTokenize("hello world");
        Assert.AreEqual(101, tokens[0], "First token should be [CLS] (101)");
        Assert.AreEqual(102, tokens[^1], "Last token should be [SEP] (102)");
    }

    [TestMethod]
    public void SimpleTokenize_TruncatesLongInput()
    {
        var longInput = string.Join(" ", Enumerable.Range(0, 500).Select(i => $"word{i}"));
        var tokens = OnnxEmbedder.SimpleTokenize(longInput);
        Assert.IsLessThanOrEqualTo(128, tokens.Length, $"Token count {tokens.Length} exceeds max 128");
    }

    [TestMethod]
    public void SimpleTokenize_EmptyInput_ReturnsClsSep()
    {
        var tokens = OnnxEmbedder.SimpleTokenize("");
        Assert.AreEqual(101, tokens[0]);
        Assert.AreEqual(102, tokens[^1]);
    }

    // ─── Batch embedding tests ───────────────────────────────────────────────

    [TestMethod]
    [TestCategory("Unit")]
    public async Task EmbedBatchAsync_WithoutModel_ReturnsZeroVectors()
    {
        var embedder = new OnnxEmbedder((Microsoft.ML.OnnxRuntime.InferenceSession?)null,
            NullLogger<OnnxEmbedder>.Instance);
        var texts = new List<string> { "hello", "world", "test" };
        var results = await embedder.EmbedBatchAsync(texts);

        Assert.HasCount(3, results);
        foreach (var result in results)
        {
            Assert.HasCount(384, result);
            Assert.IsTrue(result.All(static v => v == 0f));
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task EmbedBatchAsync_EmptyList_ReturnsEmptyList()
    {
        var embedder = new OnnxEmbedder((Microsoft.ML.OnnxRuntime.InferenceSession?)null,
            NullLogger<OnnxEmbedder>.Instance);
        var results = await embedder.EmbedBatchAsync(new List<string>());
        Assert.IsEmpty(results);
    }

    // ─── SessionOptions auto-detection tests ─────────────────────────────────

    [TestMethod]
    [TestCategory("Unit")]
    public void CreateSessionOptions_AutoDetect_SetsThreadsToHalfCpus()
    {
        var searchOptions = new SearchOptions { OnnxThreadCount = 0 };
        var (options, providerName) = OnnxEmbedder.CreateSessionOptions(searchOptions);

        int expected = Math.Clamp(Environment.ProcessorCount / 2, 1, 16);
        Assert.AreEqual(expected, options.IntraOpNumThreads);
        Assert.AreEqual(1, options.InterOpNumThreads);
        Assert.IsFalse(string.IsNullOrEmpty(providerName));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CreateSessionOptions_ExplicitThreadCount_Honored()
    {
        var searchOptions = new SearchOptions { OnnxThreadCount = 6 };
        var (options, providerName) = OnnxEmbedder.CreateSessionOptions(searchOptions);

        Assert.AreEqual(6, options.IntraOpNumThreads);
        Assert.AreEqual(1, options.InterOpNumThreads);
        Assert.IsFalse(string.IsNullOrEmpty(providerName));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CreateSessionOptions_GraphOptimization_EnabledAll()
    {
        var searchOptions = new SearchOptions { OnnxThreadCount = 2 };
        var (options, _) = OnnxEmbedder.CreateSessionOptions(searchOptions);

        Assert.AreEqual(Microsoft.ML.OnnxRuntime.GraphOptimizationLevel.ORT_ENABLE_ALL,
            options.GraphOptimizationLevel);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CreateSessionOptions_ReturnsProviderName()
    {
        var searchOptions = new SearchOptions { OnnxThreadCount = 2 };
        var (_, providerName) = OnnxEmbedder.CreateSessionOptions(searchOptions);

        // In CI/test environment without GPU, should fall back to CPU
        Assert.AreEqual("CPU", providerName);
    }

    // ─── CPU feature detection tests ─────────────────────────────────────────

    [TestMethod]
    [TestCategory("Unit")]
    public void DetectCpuFeatures_ReturnsNonEmptyList()
    {
        var features = OnnxEmbedder.DetectCpuFeatures();
        Assert.IsNotEmpty(features);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void DetectCpuFeatures_ContainsOnlyKnownFlags()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sse4_1", "sse4_2", "avx", "avx2", "avx512f", "avx512bw", "avx512vl",
            "avx512_vnni", "avx_vnni", "avx512vbmi", "amx_int8", "amx_bf16", "fma", "f16c",
            "neon", "dotprod"
        };
        var features = OnnxEmbedder.DetectCpuFeatures();
        foreach (var f in features)
        {
            Assert.Contains(f, known);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CpuFeatures_Property_IsSameAsDetect()
    {
        var embedder = new OnnxEmbedder((Microsoft.ML.OnnxRuntime.InferenceSession?)null,
            NullLogger<OnnxEmbedder>.Instance);
        Assert.IsNotEmpty(embedder.CpuFeatures);
    }
}
