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
    string SelectedModelFile { get; }
    IReadOnlyList<string> CpuFeatures { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}

public class OnnxEmbedder : IOnnxEmbedder, IDisposable
{
    public const int EmbeddingDimensions = 384;
    private const string ModelFileName = "model.onnx";
    private const string HuggingFaceOnnxBase =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/";

    // Architecture-specific quantized models from HuggingFace (INT8, ~23 MB each).
    // Ordered by preference: best-match first, broadest-compat last.
    private static readonly string[] QuantizedModelFileNames = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => ["model_qint8_arm64.onnx"],
        _ => ["model_qint8_avx512_vnni.onnx", "model_qint8_avx512.onnx", "model_quint8_avx2.onnx"]
    };

    // For auto-download: pick the broadest-compat quantized model per architecture.
    private static readonly (string FileName, string Url) DefaultQuantizedDownload = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 =>
            ("model_qint8_arm64.onnx", HuggingFaceOnnxBase + "model_qint8_arm64.onnx"),
        _ =>
            ("model_quint8_avx2.onnx", HuggingFaceOnnxBase + "model_quint8_avx2.onnx")
    };

    private readonly ILogger<OnnxEmbedder> _logger;
    private InferenceSession? _session;
    private bool _disposed;

    public int Dimensions => EmbeddingDimensions;
    public bool IsAvailable => _session != null;
    public string ExecutionProvider { get; private set; } = "none";
    public int ThreadCount { get; private set; }
    public int BatchSize { get; private set; }
    public string ModelType { get; private set; } = "none";
    public string SelectedModelFile { get; private set; } = "none";
    public IReadOnlyList<string> CpuFeatures { get; } = DetectCpuFeatures();

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
                SelectedModelFile = Path.GetFileName(modelPath);
                ModelType = modelPath.Contains("qint8", StringComparison.OrdinalIgnoreCase)
                         || modelPath.Contains("quint8", StringComparison.OrdinalIgnoreCase)
                    ? "INT8-quantized" : "FP32";

                _logger.LogInformation(
                    "ONNX model loaded: provider={Provider}, model={ModelType}, file={FileName}, threads={Threads}, cpus={Cpus}, cpuFeatures=[{CpuFeatures}], path='{Path}'.",
                    ExecutionProvider, ModelType, SelectedModelFile, ThreadCount, Environment.ProcessorCount, string.Join(", ", CpuFeatures), modelPath);

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
    /// Execution providers: tries CUDA → OpenVINO → CPU based on platform and hardware.
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
        var providerName = TryAppendBestExecutionProvider(options, searchOptions, logger);

        return (options, providerName);
    }

    internal static string TryAppendBestExecutionProvider(OrtSessionOptions options, SearchOptions searchOptions, ILogger? logger)
    {
        var provider = searchOptions.OnnxProvider?.Trim() ?? "auto";

        // Validate supported values
        var supported = new[] { "auto", "cuda", "openvino", "cpu" };
        if (!supported.Any(s => string.Equals(provider, s, StringComparison.OrdinalIgnoreCase)))
        {
            logger?.LogWarning("Unknown OnnxProvider '{Provider}'. Supported values: {Supported}. Falling back to auto.",
                provider, string.Join(", ", supported));
            provider = "auto";
        }

        // Explicit provider override — skip auto-detection
        if (string.Equals(provider, "cuda", StringComparison.OrdinalIgnoreCase))
        {
            if (TryAppendCuda(options, logger)) return "CUDA";
            logger?.LogWarning("CUDA provider requested but not available. Falling back to CPU.");
            return "CPU";
        }
        if (string.Equals(provider, "openvino", StringComparison.OrdinalIgnoreCase))
        {
            if (TryAppendOpenVino(options, logger)) return "OpenVINO";
            logger?.LogWarning("OpenVINO provider requested but not available. Falling back to CPU.");
            return "CPU";
        }
        if (string.Equals(provider, "cpu", StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogInformation("ONNX using CPU execution provider (forced).");
            return "CPU";
        }

        // Auto-detection: CUDA → OpenVINO → CPU
        // CoreML was tested on Apple Silicon and found to be slower than CPU-only
        // for small models (MiniLM-L6-v2), while burning ~300% CPU at idle.
        // Intel iGPU was tested and found ~20x slower than CPU AVX-VNNI for INT8 models.
        if (TryAppendCuda(options, logger)) return "CUDA";
        if (TryAppendOpenVino(options, logger)) return "OpenVINO";

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
        // OpenVINO requires Intel.ML.OnnxRuntime.OpenVino NuGet (Windows) or a custom Linux build
        // with OpenVINO enabled. The AppendExecutionProvider_OpenVINO extension method may not be
        // available or may throw if the native OpenVINO libraries are missing or the platform is
        // not supported. We call it directly and rely on exception handling to avoid hard failures.
        //
        // Always targets CPU. Intel iGPU was tested (i9-14900K) and found ~20x slower than
        // CPU AVX-VNNI for INT8-quantized models due to OpenCL compilation overhead and
        // CPU↔GPU memory transfer latency.

        try
        {
            options.AppendExecutionProvider_OpenVINO("CPU");
            logger?.LogInformation("ONNX OpenVINO execution provider enabled (device: CPU).");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Detects CPU instruction set features relevant to ONNX Runtime performance.
    /// On Linux, reads /proc/cpuinfo flags. On other platforms, uses .NET intrinsics probes.
    /// </summary>
    internal static IReadOnlyList<string> DetectCpuFeatures()
    {
        var features = new List<string>();

        if (OperatingSystem.IsLinux())
        {
            // /proc/cpuinfo is the most reliable source on Linux (works inside containers too)
            try
            {
                var cpuinfo = File.ReadAllText("/proc/cpuinfo");
                var flagsLine = cpuinfo.Split('\n').FirstOrDefault(l => l.StartsWith("flags", StringComparison.OrdinalIgnoreCase));
                if (flagsLine != null)
                {
                    var flags = flagsLine.Split(':').ElementAtOrDefault(1)?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
                    var relevant = new[] { "sse4_1", "sse4_2", "avx", "avx2", "avx512f", "avx512bw", "avx512vl", "avx512_vnni", "avx_vnni", "amx_int8", "amx_bf16", "fma", "f16c" };
                    foreach (var flag in relevant)
                    {
                        if (flags.Contains(flag, StringComparer.OrdinalIgnoreCase))
                            features.Add(flag);
                    }
                }
            }
            catch { /* /proc/cpuinfo not readable */ }
        }

        if (features.Count == 0)
        {
            // Fallback: .NET hardware intrinsics (works cross-platform)
            if (System.Runtime.Intrinsics.X86.Sse41.IsSupported) features.Add("sse4_1");
            if (System.Runtime.Intrinsics.X86.Sse42.IsSupported) features.Add("sse4_2");
            if (System.Runtime.Intrinsics.X86.Avx.IsSupported) features.Add("avx");
            if (System.Runtime.Intrinsics.X86.Avx2.IsSupported) features.Add("avx2");
            if (System.Runtime.Intrinsics.X86.Avx512F.IsSupported) features.Add("avx512f");
            if (System.Runtime.Intrinsics.X86.Avx512BW.IsSupported) features.Add("avx512bw");
            if (System.Runtime.Intrinsics.X86.Avx512Vbmi.IsSupported) features.Add("avx512vbmi");
            if (System.Runtime.Intrinsics.X86.AvxVnni.IsSupported) features.Add("avx_vnni");
            if (System.Runtime.Intrinsics.X86.Fma.IsSupported) features.Add("fma");
            if (System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported) features.Add("neon");
            if (System.Runtime.Intrinsics.Arm.Dp.IsSupported) features.Add("dotprod");
        }

        return features;
    }

    private string? ResolveModelPath(HgvMateOptions options)
    {
        // Prefer architecture-specific quantized INT8 models, then FP32 fallback
        var candidates = QuantizedModelFileNames.Append(ModelFileName);
        foreach (var fileName in candidates)
        {
            // 1. Check next to app binary
            var appPath = Path.Combine(AppContext.BaseDirectory, "models", fileName);
            if (File.Exists(appPath)) return appPath;

            // 2. Check in data path
            var dataPath = Path.Combine(options.DataPath, "models", fileName);
            if (File.Exists(dataPath)) return dataPath;
        }

        // 3. Auto-download broadest-compat quantized model to data path
        var (downloadName, downloadUrl) = DefaultQuantizedDownload;
        var downloadPath = Path.Combine(options.DataPath, "models", downloadName);
        _logger.LogInformation("ONNX model not found locally. Downloading {Model} from Hugging Face...", downloadName);
        try
        {
            var modelsDir = Path.GetDirectoryName(downloadPath)!;
            Directory.CreateDirectory(modelsDir);

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(10);

            var tempPath = downloadPath + ".download";
            // NOTE: Synchronous download in constructor. The model is pre-baked in Docker images,
            // so this path only runs in local dev when the model is missing. WarmupService handles
            // async startup; this fallback blocks briefly to ensure the embedder is ready.
            using (var response = httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
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

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_session == null)
            return new float[EmbeddingDimensions];

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                var tokens = SimpleTokenize(text);
                var embedding = RunSingleInference(tokens);
                var result = Normalize(embedding);
                sw.Stop();

                HgvMateDiagnostics.OnnxInferenceTotal.Add(1);
                HgvMateDiagnostics.OnnxInferenceDuration.Record(sw.Elapsed.TotalMilliseconds);

                return result;
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embedding failed.");
            return new float[EmbeddingDimensions];
        }
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (_session == null || texts.Count == 0)
            return texts.Select(_ => new float[EmbeddingDimensions]).ToList();

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
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

                return (IReadOnlyList<float[]>)embeddings;
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch embedding failed for {Count} texts.", texts.Count);
            return texts.Select(_ => new float[EmbeddingDimensions]).ToList();
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
        double sumSq = 0;
        for (int i = 0; i < v.Length; i++)
            sumSq += (double)v[i] * v[i];
        if (sumSq < 1e-12) return v;
        float norm = (float)Math.Sqrt(sumSq);
        var result = new float[v.Length];
        for (int i = 0; i < v.Length; i++)
            result[i] = v[i] / norm;
        return result;
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
