using HgvMate.Mcp.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HgvMate.Mcp.Search;

public interface IOnnxEmbedder
{
    int Dimensions { get; }
    bool IsAvailable { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

public class OnnxEmbedder : IOnnxEmbedder, IDisposable
{
    public const int EmbeddingDimensions = 384;
    private readonly ILogger<OnnxEmbedder> _logger;
    private InferenceSession? _session;
    private bool _disposed;

    public int Dimensions => EmbeddingDimensions;
    public bool IsAvailable => _session != null;

    public OnnxEmbedder(HgvMateOptions options, ILogger<OnnxEmbedder> logger)
    {
        _logger = logger;
        var modelPath = Path.Combine(AppContext.BaseDirectory, "models", "all-MiniLM-L6-v2.onnx");

        if (!File.Exists(modelPath))
            modelPath = Path.Combine(options.DataPath, "models", "all-MiniLM-L6-v2.onnx");

        if (File.Exists(modelPath))
        {
            try
            {
                _session = new InferenceSession(modelPath);
                _logger.LogInformation("ONNX model loaded from '{Path}'.", modelPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load ONNX model. Semantic search will be disabled.");
            }
        }
        else
        {
            _logger.LogInformation(
                "ONNX model not found at '{Path}'. Semantic search will be disabled. Text search remains available.",
                modelPath);
        }
    }

    // Internal constructor for testing: allows injecting a session directly.
    internal OnnxEmbedder(InferenceSession? session, ILogger<OnnxEmbedder> logger)
    {
        _session = session;
        _logger = logger;
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_session == null)
            return Task.FromResult(new float[EmbeddingDimensions]);

        try
        {
            var tokens = SimpleTokenize(text);
            var inputIds = new DenseTensor<long>(new[] { 1, tokens.Length });
            var attentionMask = new DenseTensor<long>(new[] { 1, tokens.Length });
            var tokenTypeIds = new DenseTensor<long>(new[] { 1, tokens.Length });

            for (int i = 0; i < tokens.Length; i++)
            {
                inputIds[0, i] = tokens[i];
                attentionMask[0, i] = 1;
                tokenTypeIds[0, i] = 0;
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
                NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds)
            };

            using var results = _session.Run(inputs);
            var output = results.First().AsTensor<float>();

            // Mean pooling over token dimension
            var embedding = new float[EmbeddingDimensions];
            int seqLen = tokens.Length;
            for (int d = 0; d < EmbeddingDimensions; d++)
            {
                float sum = 0;
                for (int t = 0; t < seqLen; t++)
                    sum += output[0, t, d];
                embedding[d] = sum / seqLen;
            }

            return Task.FromResult(Normalize(embedding));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embedding failed.");
            return Task.FromResult(new float[EmbeddingDimensions]);
        }
    }

    private static float[] Normalize(float[] v)
    {
        double sumSq = v.Sum(x => (double)x * x);
        if (sumSq < 1e-12) return v;
        float norm = (float)Math.Sqrt(sumSq);
        return v.Select(x => x / norm).ToArray();
    }

    // Minimal whitespace tokenizer — real BERT BPE tokenizer required for production use.
    private static long[] SimpleTokenize(string text)
    {
        var words = text.Split([' ', '\n', '\r', '\t', '.', ',', '(', ')', '{', '}', ';'],
            StringSplitOptions.RemoveEmptyEntries);
        var tokens = new long[Math.Min(words.Length + 2, 128)]; // [CLS] ... [SEP], max 128
        tokens[0] = 101; // [CLS]
        int i = 1;
        foreach (var word in words.Take(126))
            tokens[i++] = Math.Abs((long)word.GetHashCode()) % 30000 + 1000;
        tokens[i] = 102; // [SEP]
        return tokens[..(i + 1)];
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _session?.Dispose();
            _disposed = true;
        }
    }
}
