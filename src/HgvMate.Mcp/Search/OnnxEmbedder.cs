using System.Diagnostics;
using HgvMate.Mcp.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OrtSessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace HgvMate.Mcp.Search;

public interface IOnnxEmbedder
{
    int Dimensions { get; }
    bool IsAvailable { get; }
    string ExecutionProvider { get; }
    int ThreadCount { get; }
    int BatchSize { get; }
    string ModelType { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}

public class OnnxEmbedder : IOnnxEmbedder, IDisposable
{
    public const int EmbeddingDimensions = 384;
    private const string ModelFileName = "all-MiniLM-L6-v2.onnx";
    private const string QuantizedModelFileName = "all-MiniLM-L6-v2-quantized.onnx";
    private const string ModelDownloadUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";
    private const string QuantizedModelDownloadUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model_quantized.onnx";

    private readonly ILogger<OnnxEmbedder> _logger;
    private InferenceSession? _session;
    private bool _disposed;

    public int Dimensions => EmbeddingDimensions;
    public bool IsAvailable => _session != null;
    public string ExecutionProvider { get; private set; } = "none";
    public int ThreadCount { get; private set; }
    public int BatchSize { get; private set; }
    public string ModelType { get; private set; } = "none";

    public OnnxEmbedder(HgvMateOptions options, SearchOptions searchOptions, ILogger<OnnxEmbedder> logger)
    {
        _logger = logger;
        var modelPath = ResolveModelPath(options);

        BatchSize = searchOptions.OnnxBatchSize;

        if (modelPath != null && File.Exists(modelPath))
        {
            try
            {
                var (sessionOptions, providerName) = CreateSessionOptions(searchOptions, _logger);
                _session = new InferenceSession(modelPath, sessionOptions);

                ExecutionProvider = providerName;
                ThreadCount = sessionOptions.IntraOpNumThreads;
                ModelType = modelPath.Contains("quantized", StringComparison.OrdinalIgnoreCase)
                    ? "INT8-quantized" : "FP32";

                _logger.LogInformation(
                    "ONNX model loaded: provider={Provider}, model={ModelType}, threads={Threads}, cpus={Cpus}, path='{Path}'.",
                    ExecutionProvider, ModelType, ThreadCount, Environment.ProcessorCount, modelPath);

                // Publish to shared telemetry gauges
                HgvMateDiagnostics.SetOnnxProvider(ExecutionProvider);
                HgvMateDiagnostics.SetOnnxThreadCount(ThreadCount);
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

    /// <summary>
    /// Creates optimized SessionOptions with auto-detected thread count and execution providers.
    /// Thread count: if OnnxThreadCount is 0 (auto), uses half of available CPUs (clamped 1-16).
    /// Execution providers: tries CUDA → CoreML → CPU based on platform and hardware.
    /// </summary>
    internal static (OrtSessionOptions Options, string ProviderName) CreateSessionOptions(SearchOptions searchOptions, ILogger? logger = null)
    {
        var options = new OrtSessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

        // ── Thread auto-detection ────────────────────────────────────────
        int threadCount = searchOptions.OnnxThreadCount;
        if (threadCount <= 0)
        {
            // Auto: use half available CPUs, leaving headroom for git/GitNexus/web
            threadCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 16);
            logger?.LogInformation("ONNX auto-detected {Cpus} CPUs, using {Threads} threads for inference.",
                Environment.ProcessorCount, threadCount);
        }
        options.IntraOpNumThreads = threadCount;
        options.InterOpNumThreads = 1;

        // ── Execution provider auto-detection ────────────────────────────
        var providerName = TryAppendBestExecutionProvider(options, logger);

        return (options, providerName);
    }

    internal static string TryAppendBestExecutionProvider(OrtSessionOptions options, ILogger? logger)
    {
        // Priority: CUDA (NVIDIA GPU) → OpenVINO (Intel CPU/iGPU) → CoreML (Apple Silicon) → CPU (default)
        // Each provider is tried and silently skipped if native libs are not available.
        // The CPU execution provider is always present as fallback.

        if (TryAppendCuda(options, logger)) return "CUDA";
        if (TryAppendOpenVino(options, logger)) return "OpenVINO";
        if (TryAppendCoreML(options, logger)) return "CoreML";

        logger?.LogInformation("ONNX using CPU execution provider.");
        return "CPU";
    }

    private static bool TryAppendCuda(OrtSessionOptions options, ILogger? logger)
    {
        try
        {
            options.AppendExecutionProvider_CUDA(0);
            logger?.LogInformation("ONNX CUDA execution provider enabled (GPU 0).");
            return true;
        }
        catch (Exception)
        {
            // CUDA not available — no NVIDIA GPU or driver not installed
            return false;
        }
    }

    private static bool TryAppendOpenVino(OrtSessionOptions options, ILogger? logger)
    {
        // OpenVINO requires Intel.ML.OnnxRuntime.OpenVino NuGet (Windows) or custom Linux build.
        // The AppendExecutionProvider_OpenVINO method is only available when the OpenVINO native
        // libs are present. We use reflection to avoid a hard compile-time dependency.
        try
        {
            // Try the standard OpenVINO API — will throw if not available
            options.AppendExecutionProvider_OpenVINO("CPU");
            logger?.LogInformation("ONNX OpenVINO execution provider enabled (Intel CPU).");
            return true;
        }
        catch (Exception)
        {
            // OpenVINO not available — package not installed or wrong platform
            return false;
        }
    }

    private static bool TryAppendCoreML(OrtSessionOptions options, ILogger? logger)
    {
        if (!OperatingSystem.IsMacOS()) return false;

        try
        {
            options.AppendExecutionProvider_CoreML();
            logger?.LogInformation("ONNX CoreML execution provider enabled (Apple Silicon).");
            return true;
        }
        catch (Exception)
        {
            // CoreML not available
            return false;
        }
    }

    private string? ResolveModelPath(HgvMateOptions options)
    {
        // Prefer quantized INT8 model for faster CPU inference
        foreach (var fileName in new[] { QuantizedModelFileName, ModelFileName })
        {
            // 1. Check next to app binary
            var appPath = Path.Combine(AppContext.BaseDirectory, "models", fileName);
            if (File.Exists(appPath)) return appPath;

            // 2. Check in data path
            var dataPath = Path.Combine(options.DataPath, "models", fileName);
            if (File.Exists(dataPath)) return dataPath;
        }

        // 3. Auto-download quantized model to data path (smaller + faster on CPU)
        var downloadPath = Path.Combine(options.DataPath, "models", QuantizedModelFileName);
        _logger.LogInformation("ONNX model not found locally. Downloading quantized model from Hugging Face...");
        try
        {
            var modelsDir = Path.GetDirectoryName(downloadPath)!;
            Directory.CreateDirectory(modelsDir);

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(10);

            var tempPath = downloadPath + ".download";
            using (var response = httpClient.GetAsync(QuantizedModelDownloadUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                using var stream = response.Content.ReadAsStream();
                using var fileStream = File.Create(tempPath);
                stream.CopyTo(fileStream);
            }

            File.Move(tempPath, downloadPath, overwrite: true);
            _logger.LogInformation("Quantized ONNX model downloaded to '{Path}'.", downloadPath);
            return downloadPath;
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
            var sw = Stopwatch.StartNew();
            var tokens = SimpleTokenize(text);
            var embedding = RunSingleInference(tokens);
            var result = Normalize(embedding);
            sw.Stop();

            HgvMateDiagnostics.OnnxInferenceTotal.Add(1);
            HgvMateDiagnostics.OnnxInferenceDuration.Record(sw.Elapsed.TotalMilliseconds);

            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embedding failed.");
            return Task.FromResult(new float[EmbeddingDimensions]);
        }
    }

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (_session == null || texts.Count == 0)
            return Task.FromResult<IReadOnlyList<float[]>>(
                texts.Select(_ => new float[EmbeddingDimensions]).ToList());

        try
        {
            var sw = Stopwatch.StartNew();

            // Tokenize all texts
            var allTokens = texts.Select(SimpleTokenize).ToList();
            int maxLen = allTokens.Max(t => t.Length);
            int batchSize = texts.Count;

            // Create padded batched tensors
            var inputIds = new DenseTensor<long>(new[] { batchSize, maxLen });
            var attentionMask = new DenseTensor<long>(new[] { batchSize, maxLen });
            var tokenTypeIds = new DenseTensor<long>(new[] { batchSize, maxLen });

            for (int b = 0; b < batchSize; b++)
            {
                var tokens = allTokens[b];
                for (int i = 0; i < maxLen; i++)
                {
                    if (i < tokens.Length)
                    {
                        inputIds[b, i] = tokens[i];
                        attentionMask[b, i] = 1;
                    }
                    // Padding positions stay 0 (default for DenseTensor)
                }
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
                NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds)
            };

            using var results = _session.Run(inputs);
            var output = results.First().AsTensor<float>();

            // Extract and normalize embeddings for each item in batch
            var embeddings = new List<float[]>(batchSize);
            for (int b = 0; b < batchSize; b++)
            {
                var embedding = new float[EmbeddingDimensions];
                int seqLen = allTokens[b].Length;
                for (int d = 0; d < EmbeddingDimensions; d++)
                {
                    float sum = 0;
                    for (int t = 0; t < seqLen; t++)
                        sum += output[b, t, d];
                    embedding[d] = sum / seqLen;
                }
                embeddings.Add(Normalize(embedding));
            }

            sw.Stop();
            HgvMateDiagnostics.OnnxInferenceTotal.Add(1);
            HgvMateDiagnostics.OnnxBatchDuration.Record(sw.Elapsed.TotalMilliseconds);

            return Task.FromResult<IReadOnlyList<float[]>>(embeddings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch embedding failed for {Count} texts.", texts.Count);
            return Task.FromResult<IReadOnlyList<float[]>>(
                texts.Select(_ => new float[EmbeddingDimensions]).ToList());
        }
    }

    private float[] RunSingleInference(long[] tokens)
    {
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

        using var results = _session!.Run(inputs);
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

        return embedding;
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
