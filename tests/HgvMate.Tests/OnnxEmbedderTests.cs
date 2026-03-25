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
        Assert.AreEqual(384, result.Length);
        Assert.IsTrue(result.All(v => v == 0f));
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
        Assert.IsTrue(tokens.Length <= 128, $"Token count {tokens.Length} exceeds max 128");
    }

    [TestMethod]
    public void SimpleTokenize_EmptyInput_ReturnsClsSep()
    {
        var tokens = OnnxEmbedder.SimpleTokenize("");
        Assert.AreEqual(101, tokens[0]);
        Assert.AreEqual(102, tokens[^1]);
    }
}
