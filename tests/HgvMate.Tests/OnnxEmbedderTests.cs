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
}
