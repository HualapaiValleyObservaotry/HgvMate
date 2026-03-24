# HgvMate

An MCP (Model Context Protocol) server that provides AI-powered tools access to HGV source code repositories, database logs, and Azure DevOps work items. Designed to be consumed by VS Code Copilot Chat or any MCP-compatible client.

## Architecture

See [docs/development-plan.md](docs/development-plan.md) for the full design and development plan.

## Quick Start

### Local (stdio)

```bash
dotnet run --project src/HgvMate.Mcp
```

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
    "hgvmate": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "projects/HgvMate/src/HgvMate.Mcp"]
    }
  }
}
```

## Projects

| Project | Description |
|---------|-------------|
| `HgvMate.Mcp` | MCP server entry point — tool definitions, search, repo sync |
| `HgvMate.Tests` | MSTest unit and integration tests |

## Tech Stack

- .NET 10
- MCP SDK (`ModelContextProtocol`)
- SQLite + sqlite-vec (vector search)
- ONNX Runtime (local embeddings)
- Docker (containerized deployment)
