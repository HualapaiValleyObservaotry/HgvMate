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
    private const string ModelFileName = "all-MiniLM-L6-v2.onnx";
    private const string ModelDownloadUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";

    private readonly ILogger<OnnxEmbedder> _logger;
    private InferenceSession? _session;
    private bool _disposed;

    public int Dimensions => EmbeddingDimensions;
    public bool IsAvailable => _session != null;

    public OnnxEmbedder(HgvMateOptions options, ILogger<OnnxEmbedder> logger)
    {
        _logger = logger;
        var modelPath = ResolveModelPath(options);

        if (modelPath != null && File.Exists(modelPath))
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
            _logger.LogWarning(
                "ONNX model could not be resolved. Semantic search will be disabled. Text search remains available.");
        }
    }

    private string? ResolveModelPath(HgvMateOptions options)
    {
        // 1. Check next to app binary
        var appPath = Path.Combine(AppContext.BaseDirectory, "models", ModelFileName);
        if (File.Exists(appPath)) return appPath;

        // 2. Check in data path
        var dataPath = Path.Combine(options.DataPath, "models", ModelFileName);
        if (File.Exists(dataPath)) return dataPath;

        // 3. Auto-download to data path
        _logger.LogInformation("ONNX model not found locally. Downloading from Hugging Face...");
        try
        {
            var modelsDir = Path.GetDirectoryName(dataPath)!;
            Directory.CreateDirectory(modelsDir);

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(10);

            var tempPath = dataPath + ".download";
            using (var response = httpClient.GetAsync(ModelDownloadUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                using var stream = response.Content.ReadAsStream();
                using var fileStream = File.Create(tempPath);
                stream.CopyTo(fileStream);
            }

            File.Move(tempPath, dataPath, overwrite: true);
            _logger.LogInformation("ONNX model downloaded to '{Path}'.", dataPath);
            return dataPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download ONNX model. Semantic search will be disabled.");
            return null;
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

    // Minimal whitespace tokenizer — maps words to token IDs within the model's vocabulary range.
    // all-MiniLM-L6-v2 has a vocabulary size of 30,522 (standard BERT uncased).
    private const int VocabSize = 30522;

    internal static long[] SimpleTokenize(string text)
    {
        var words = text.Split([' ', '\n', '\r', '\t', '.', ',', '(', ')', '{', '}', ';'],
            StringSplitOptions.RemoveEmptyEntries);
        var tokens = new long[Math.Min(words.Length + 2, 128)]; // [CLS] ... [SEP], max 128
        tokens[0] = 101; // [CLS]
        int i = 1;
        foreach (var word in words.Take(126))
        {
            // Map to valid range [1, VocabSize-1], avoiding special tokens 0-999
            var hash = (uint)word.ToLowerInvariant().GetHashCode();
            tokens[i++] = (long)(hash % (VocabSize - 1000)) + 1000;
        }
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
