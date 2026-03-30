# HgvMate

An MCP (Model Context Protocol) server that provides AI agents with source code search, structural code intelligence, file access, and repository management. Designed to be consumed by VS Code Copilot Chat or any MCP-compatible client.

## Features

- **14 MCP tools** prefixed `hgvmate_*` — repository management, search, file access, structural analysis, server info, usage analytics
- **Dual transport** — stdio (local dev) and SSE/HTTP (remote/container deployment)
- **Triple-indexed search** — git grep (text), ONNX embeddings (semantic), GitNexus (structural)
- **REST API** with OpenAPI/Scalar documentation alongside MCP
- **ONNX model** — all-MiniLM-L6-v2 (384-dim embeddings, ~80 MB, auto-downloads from Hugging Face)
- **SQLite** — single-file database with binary BLOB vector storage and in-memory cache
- **Docker** — Ubuntu multi-stage build with ONNX model baked in, multi-arch (amd64 + arm64)
- **In-memory vector cache** — pre-loads embeddings at startup for sub-10ms search
- **Tool usage logging** — SQLite-backed analytics for monitoring MCP tool usage patterns
- **116 tests** — unit, integration, protocol, SSE, REST API, live ONNX, and Docker tests

## Architecture

See [docs/development-plan.md](docs/development-plan.md) for the full design and development plan.  
See [.hgvmate/techstack.yml](.hgvmate/techstack.yml) for the technology stack metadata (consumed by `hgvmate_get_techstack`).

## Quick Start

### Local (stdio)

```bash
dotnet run --project src/HgvMate.Mcp
```

### Local (SSE + REST API)

```bash
HGVMATE_TRANSPORT=sse dotnet run --project src/HgvMate.Mcp
```

Endpoints:
- MCP: `http://localhost:5000/mcp`
- REST API: `http://localhost:5000/api/*`
- OpenAPI doc: `http://localhost:5000/openapi/v1.json`
- API reference UI: `http://localhost:5000/scalar/v1`

### Docker

```bash
docker build -t hgvmate-mcp .
docker run -i --rm -e AZURE_DEVOPS_PAT=your-pat -v hgvmate-data:/data hgvmate-mcp
```

### Docker Compose (recommended)

```bash
docker compose up -d
```

The included `docker-compose.yml` sets recommended resource limits (2 vCPU, 2 GB RAM, 20 GB data volume) and exposes the REST API + MCP on port 5000.

## Deployment Recommendations

| Resource | Baseline (up to 20 repos) | Scaled (30+ repos) |
|----------|--------------------------|---------------------|
| **CPU** | 2 vCPUs | 4+ vCPUs |
| **Memory** | 4 GB | 6 GB |
| **Data volume** | 20 GB | 40 GB+ |

Resource limits **cannot** be set in the Dockerfile — they are applied at deployment time via `docker run` flags, `docker-compose.yml`, or your orchestrator (Kubernetes, Proxmox, etc.).

**With `docker run`:**

```bash
docker run -d \
  --cpus=2 --memory=4g \
  -e HGVMATE_TRANSPORT=sse \
  -p 5000:5000 \
  -v hgvmate-data:/data \
  hgvmate-mcp
```

**Health check:** `GET /health` returns system status including repo sync state, vector cache size, disk space, and embedder availability.

### VS Code MCP Config

Add to `.vscode/mcp.json`:

```json
{
  "servers": {
    "hgvmate-stdio": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "src/HgvMate.Mcp"]
    },
    "hgvmate-docker": {
      "type": "stdio",
      "command": "docker",
      "args": ["run", "-i", "--rm", "-v", "hgvmate-data:/data", "hgvmate-mcp"]
    },
    "hgvmate-sse": {
      "type": "sse",
      "url": "http://localhost:5000/mcp"
    }
  }
}
```

## MCP Tools

| Tool | Description |
|------|-------------|
| `hgvmate_add_repository` | Add a repo to be indexed (source: github or azuredevops) |
| `hgvmate_remove_repository` | Remove a repo and its cloned data |
| `hgvmate_list_repositories` | List repos with sync status |
| `hgvmate_reindex` | Force sync + re-index for one or all repos |
| `hgvmate_index_status` | Per-repo index status |
| `hgvmate_search_source_code` | Hybrid text + semantic search |
| `hgvmate_get_file_content` | Read a source file from a cloned repo |
| `hgvmate_find_symbol` | Symbol view: callers, callees, hierarchy |
| `hgvmate_get_references` | What calls/uses a symbol |
| `hgvmate_get_call_chain` | Execution flow trace |
| `hgvmate_get_impact` | Blast radius analysis |
| `hgvmate_server_info` | Server version, capabilities, and endpoint info |
| `hgvmate_usage_report` | Tool usage analytics — call counts, patterns, error rates |

## REST API

When running in SSE/HTTP mode, a REST API is available alongside MCP at `/api/*`:

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/repositories` | List all repositories |
| POST | `/api/repositories` | Add a repository |
| DELETE | `/api/repositories/{name}` | Remove a repository |
| POST | `/api/repositories/{name}/reindex` | Reindex a repository |
| POST | `/api/repositories/reindex` | Reindex all repositories |
| GET | `/api/repositories/{name}/status` | Repository index status |
| GET | `/api/status` | All repositories status |
| GET | `/health` | System health check |
| GET | `/api/search?query=...&repository=...` | Search source code |
| GET | `/api/repositories/{repo}/files/{path}` | Read a file |
| GET | `/api/symbols/{name}?repository=...` | Find symbol |
| GET | `/api/references/{name}?repository=...` | Get references |
| GET | `/api/callchain/{name}?repository=...` | Call chain trace |
| GET | `/api/impact/{name}?repository=...` | Blast radius |
| GET | `/api/server-info` | Server version, capabilities, endpoints |
| GET | `/api/diagnostics` | Live telemetry statistics |
| GET | `/api/diagnostics/usage` | Tool usage summary |
| GET | `/api/diagnostics/usage/patterns` | Usage patterns — repeated searches, tool sequences |

## Configuration

| Environment Variable | Default | Description |
|---------------------|---------|-------------|
| `HGVMATE_DATA_PATH` | `./data` | Data directory for SQLite DB and cloned repos |
| `HGVMATE_TRANSPORT` | `stdio` | Transport: `stdio`, `sse`, or `http` |
| `GITHUB_TOKEN` | — | GitHub PAT for cloning private repos |
| `AZURE_DEVOPS_PAT` | — | Azure DevOps PAT for cloning private repos |

## Testing

```bash
# Run all unit + integration tests
dotnet test

# Run with live ONNX model (downloads ~80 MB model)
HGVMATE_LIVE_ONNX=true dotnet test --filter "TestCategory=LiveOnnx"

# Run Docker tests (requires Docker)
dotnet test --filter "TestCategory=Docker"

# Exclude slow tests
dotnet test --filter "FullyQualifiedName!~DockerTests&FullyQualifiedName!~LiveOnnx"
```

## Projects

| Project | Description |
|---------|-------------|
| `HgvMate.Mcp` | MCP server — tools, search, repo sync, REST API |
| `HgvMate.Tests` | MSTest unit, integration, protocol, and E2E tests |

## Tech Stack

- .NET 10 (SDK Web)
- MCP SDK (`ModelContextProtocol` + `ModelContextProtocol.AspNetCore`)
- SQLite (binary BLOB embeddings with in-memory cache)
- ONNX Runtime (all-MiniLM-L6-v2 local embeddings)
- OpenAPI + Scalar (API documentation)
- MSTest 4.x
- Docker (Ubuntu multi-stage build)

## Deployment

### Container Image Variants

The Dockerfile supports a build argument `ONNX_PROVIDER` that controls which ONNX Runtime execution provider is used:

| `ONNX_PROVIDER` | Platforms | Use Case |
|-----------------|-----------|----------|
| `cpu` (default) | linux/amd64, linux/arm64 | **Universal** — works everywhere including Mac (Docker Desktop), Windows (WSL2), cloud VMs, any Linux host |
| `openvino` | linux/amd64 only | **Intel-optimized** — uses OpenVINO EP for accelerated inference on Intel CPUs. Best for dedicated Intel servers (e.g., Proxmox LXC with i9) |

**Building each variant:**

```bash
# Universal CPU image (default) — runs on Mac, Windows, Linux, ARM, x86
docker build -t hgvmate .

# Intel OpenVINO image — Intel x86-64 servers only
docker build --build-arg ONNX_PROVIDER=openvino -t hgvmate:openvino .
```

**Pre-built images on GHCR (pushed by CI on every merge to main):**

| Tag | Description |
|-----|-------------|
| `ghcr.io/roysalisbury/hgvmate:latest` | Universal CPU image |
| `ghcr.io/roysalisbury/hgvmate:cpu` | Same as `latest` (explicit alias) |
| `ghcr.io/roysalisbury/hgvmate:openvino` | Intel OpenVINO-optimized (x86-64 only) |
| `ghcr.io/roysalisbury/hgvmate:<sha>` | Specific commit (CPU) |
| `ghcr.io/roysalisbury/hgvmate:<sha>-openvino` | Specific commit (OpenVINO) |

**Pulling from Portainer or CLI:**

```bash
# On any machine (Mac, Proxmox, cloud VM):
docker pull ghcr.io/roysalisbury/hgvmate:latest

# On Intel servers (Proxmox, bare metal):
docker pull ghcr.io/roysalisbury/hgvmate:openvino
```

### Running on Any Machine

```bash
# Quick start — Docker auto-creates the named volume on first run
docker run -d \
  --name hgvmate \
  -e HGVMATE_TRANSPORT=sse \
  -p 5000:5000 \
  -v hgvmate-data:/data \
  ghcr.io/roysalisbury/hgvmate:latest
```

The `-v hgvmate-data:/data` syntax creates a **named volume** automatically if it doesn't exist. Docker manages its location; data persists across container restarts and upgrades. No need to `mkdir` anything.

For bind mounts (explicit host path), the directory must exist:

```bash
# Bind mount — you manage the directory, useful for backups
mkdir -p ~/hgvmate-data
docker run -d -e HGVMATE_TRANSPORT=sse -p 5000:5000 \
  -v ~/hgvmate-data:/data ghcr.io/roysalisbury/hgvmate:latest
```

### Docker Compose (recommended)

```bash
docker compose up -d        # uses docker-compose.yml (general purpose)
```

For Proxmox or dedicated Intel servers:

```bash
docker compose -f docker-compose.proxmox.yml up -d
```

### Mac-Specific Notes

- **Apple Silicon (M1/M2/M3/M4):** Use the default `cpu` image. Docker Desktop runs it via linux/arm64. ONNX models auto-select the ARM-optimized INT8 variant. CoreML is **not** used — testing showed CPU-only inference is faster and avoids ~300% idle CPU overhead.
- **Intel Mac:** Use the default `cpu` image. The `openvino` image also works but the OpenVINO EP benefit on older Intel Mac CPUs is marginal.
- Docker Desktop resource limits apply. Allocate at least 2 GB RAM and 2 CPUs in Docker Desktop → Settings → Resources.

### Platform Compatibility Matrix

| Host | Architecture | `cpu` image | `openvino` image |
|------|-------------|-------------|------------------|
| Mac (Apple Silicon) | arm64 | ✅ | ❌ (x86-64 only) |
| Mac (Intel) | amd64 | ✅ | ✅ (marginal benefit) |
| Windows (WSL2) | amd64 | ✅ | ✅ (if Intel CPU) |
| Linux (Intel/AMD) | amd64 | ✅ | ✅ (Intel CPUs only) |
| Linux (ARM server) | arm64 | ✅ | ❌ (x86-64 only) |
| Proxmox LXC (Intel) | amd64 | ✅ | ✅ **recommended** |

## OpenVINO Native Libraries

The OpenVINO execution provider requires native `.so` libraries extracted from Intel's pre-built Python wheel. These are stored as a **GitHub Release artifact** so that Docker builds and CI can download them without PyPI access.

### Current Version

| Component | Version |
|-----------|---------|
| ONNX Runtime | 1.24.1 |
| OpenVINO | 2025.4.1 |
| Release tag | `openvino-libs/v1.24.1` |
| Tarball | `linux-x64.tar.gz` (~62 MB compressed, ~191 MB extracted) |

### How to Update OpenVINO Libraries

When a new version of `onnxruntime-openvino` is released on PyPI:

**1. Extract new native libs:**

```bash
# Update the version number as needed
./tools/extract-openvino-libs.sh 1.25.0
```

This downloads the Python wheel, extracts only the CPU-essential `.so` files (no GPU/NPU/Python bindings), and creates a compressed tarball at `libs/openvino/linux-x64.tar.gz`.

**2. Upload to GitHub Releases:**

```bash
# For a new version:
gh release create openvino-libs/v1.25.0 \
  libs/openvino/linux-x64.tar.gz \
  --title "OpenVINO native libs v1.25.0 (Linux x64)" \
  --notes "Source: onnxruntime-openvino==1.25.0 from PyPI"

# Or update an existing release:
gh release upload openvino-libs/v1.25.0 \
  libs/openvino/linux-x64.tar.gz --clobber
```

**3. Update version references:**

- `Dockerfile` → `OPENVINO_LIBS_TAG` default value and `onnxruntime.dll` symlink version
- `src/HgvMate.Mcp/HgvMate.Mcp.csproj` → `Microsoft.ML.OnnxRuntime.Managed` version (must match ORT version in the wheel)
- `libs/openvino/manifest.json` → auto-updated by the extract script

**4. Rebuild and test:**

```bash
docker build --build-arg ONNX_PROVIDER=openvino -t hgvmate:openvino .
# Check logs for "provider=OpenVINO"
```

### Why This Approach

The `.so` files are **userspace libraries** that link against glibc (≥ 2.28) and standard POSIX libs — no kernel modules or kernel-specific headers. They do NOT need to be rebuilt when:
- Kernel version changes
- Docker base image gets minor updates
- .NET SDK version changes
- Application code changes

They only need updating when:
- ONNX Runtime or OpenVINO releases a new version (new features, perf improvements, bug fixes)
- Switching CPU architecture (but OpenVINO is x86-64 only anyway)
