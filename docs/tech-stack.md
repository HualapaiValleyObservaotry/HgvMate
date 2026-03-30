# HgvMate Tech Stack

## Runtime & Frameworks

| Component | Technology | Version | Purpose |
|-----------|-----------|---------|---------|
| **Application Runtime** | .NET | 10.0 | Self-contained web application |
| **Web Framework** | ASP.NET Core | 10.0 | HTTP/REST API, middleware pipeline |
| **MCP Protocol** | ModelContextProtocol SDK | 0.3.0-preview.1 | MCP server (stdio + SSE/HTTP transport) |
| **Language** | C# | 14 | Primary language |

## AI & Machine Learning

| Component | Technology | Version | Purpose |
|-----------|-----------|---------|---------|
| **Inference Runtime** | ONNX Runtime | 1.24.x | Local embedding inference |
| **Embedding Model** | all-MiniLM-L6-v2 | — | 384-dimensional text embeddings (~23 MB quantized) |
| **Execution Providers** | CPU, OpenVINO, CUDA, DirectML | — | Hardware-accelerated inference |
| **Structural Analysis** | GitNexus (tree-sitter) | 1.4.10 | AST parsing, call chains, symbol references, blast radius |
| **Node.js** | Node.js | 22.x | GitNexus runtime |

## Data & Storage

| Component | Technology | Purpose |
|-----------|-----------|---------|
| **Vector Store** | Custom binary format (`vectors.bin`) | SIMD-accelerated cosine similarity via `TensorPrimitives` |
| **Repo Metadata** | JSON files (`/data/repo-meta/*.json`) | One file per repository, async file I/O |
| **Usage Logging** | SQLite (`/data/usage.db`) | Append-only tool usage analytics |
| **In-Memory Cache** | `ConcurrentDictionary` | Pre-loaded embeddings for sub-10ms search |

## Infrastructure & Deployment

| Component | Technology | Purpose |
|-----------|-----------|---------|
| **Container** | Docker (Ubuntu 24.04) | Multi-stage build, multi-arch (amd64 + arm64) |
| **Container Registry** | GitHub Container Registry (GHCR) | Image hosting |
| **CI/CD** | GitHub Actions | Docker build + publish on push to main/tags |
| **Deployment** | Proxmox LXC + Docker Compose | Production deployment with Aspire Dashboard sidecar |
| **Networking** | Tailscale | Secure remote access to deployed instance |

## Observability

| Component | Technology | Purpose |
|-----------|-----------|---------|
| **Telemetry** | HVO.Enterprise.Telemetry + OpenTelemetry | Activities, metrics, logging |
| **Dashboard** | .NET Aspire Dashboard | OTLP trace/metric visualization |
| **Custom Metrics** | `System.Diagnostics.Metrics` | Repo sync, indexing, search, ONNX inference metrics |
| **Logging** | `ILogger<T>` + structured logging | All logging via DI, never Console.Write |

## API & Documentation

| Component | Technology | Purpose |
|-----------|-----------|---------|
| **API Documentation** | OpenAPI 3.x | Auto-generated spec at `/openapi/v1.json` |
| **API Reference UI** | Scalar | Interactive API explorer at `/scalar/v1` |
| **Rate Limiting** | ASP.NET Core Rate Limiting | Fixed-window limiter on mutating endpoints |
| **Error Responses** | RFC 7807 Problem Details | Standardized error format |

## Testing

| Component | Technology | Version | Purpose |
|-----------|-----------|---------|---------|
| **Test Framework** | MSTest | 4.x | Unit, integration, protocol tests |
| **Parallel Execution** | Method-level parallelism | — | Fast CI runs |
| **Test Categories** | Unit, Docker, Integration, LiveOnnx | — | Selective test execution |
| **Mocking** | Constructor injection + virtual methods | — | No mocking framework dependency |

## Key NuGet Packages

| Package | Purpose |
|---------|---------|
| `ModelContextProtocol` | MCP protocol implementation |
| `ModelContextProtocol.AspNetCore` | HTTP/SSE transport for MCP |
| `Microsoft.ML.OnnxRuntime` / `.Managed` | ONNX inference (CPU or OpenVINO) |
| `Microsoft.Data.Sqlite` | SQLite for usage logging |
| `Scalar.AspNetCore` | API reference UI |
| `HVO.Enterprise.Telemetry` | Enterprise telemetry framework |
| `OpenTelemetry.Instrumentation.*` | ASP.NET Core, HTTP, Process, Runtime instrumentation |

## Architecture Patterns

- **Channel\<T\> for async processing** — Tool usage logging and GitNexus analysis use bounded channels for non-blocking background work
- **Hybrid search** — git grep (text) + ONNX embeddings (semantic) + GitNexus (structural) run in parallel, results merged/deduplicated
- **Fire-and-forget with backpressure** — GitNexus analysis enqueued via bounded channel, concurrent with ONNX embedding
- **Atomic re-index** — Index-then-swap prevents data gaps during vector store updates
- **Incremental sync** — Only changed files re-indexed; GitNexus skipped for non-structural changes
