# HgvMate

An MCP (Model Context Protocol) server that provides AI agents with source code search, structural code intelligence, file access, and repository management. Designed to be consumed by VS Code Copilot Chat or any MCP-compatible client.

## Features

- **11 MCP tools** prefixed `hgvmate_*` — repository management, search, file access, structural analysis
- **Dual transport** — stdio (local dev) and SSE/HTTP (remote/container deployment)
- **Triple-indexed search** — git grep (text), ONNX embeddings (semantic), GitNexus (structural)
- **REST API** with OpenAPI/Scalar documentation alongside MCP
- **ONNX model** — all-MiniLM-L6-v2 (384-dim embeddings, ~80 MB, auto-downloads from Hugging Face)
- **SQLite + sqlite-vec** — single-file database with vector search support
- **Docker** — Alpine multi-stage build with ONNX model baked in
- **106 tests** — unit, integration, protocol, SSE, REST API, live ONNX, and Docker tests

## Architecture

See [docs/development-plan.md](docs/development-plan.md) for the full design and development plan.

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
| GET | `/api/search?query=...&repository=...` | Search source code |
| GET | `/api/repositories/{repo}/files/{path}` | Read a file |
| GET | `/api/symbols/{name}?repository=...` | Find symbol |
| GET | `/api/references/{name}?repository=...` | Get references |
| GET | `/api/callchain/{name}?repository=...` | Call chain trace |
| GET | `/api/impact/{name}?repository=...` | Blast radius |

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
- SQLite + sqlite-vec (vector search)
- ONNX Runtime (all-MiniLM-L6-v2 local embeddings)
- OpenAPI + Scalar (API documentation)
- MSTest 4.x
- Docker (Alpine multi-stage build)
