# SystemIntelligence MCP Server — Design Document

**Date:** March 24, 2026  
**Status:** Proposed  
**Branch:** `feature/tier1-auth-persistence`

---

## Overview

SystemIntelligence transitions from a Blazor Web App with an embedded AI chat interface to a **self-contained MCP (Model Context Protocol) server**. The AI orchestration and UI shifts to VS Code Copilot Chat (or any MCP-compatible client), while SystemIntelligence focuses on providing **tools** — source code search, DB log queries, and Azure DevOps work item access.

---

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                    VS Code + Copilot Chat                     │
│              (AI orchestration + analysis)                    │
│                                                              │
│   Connects to MCP servers:                                   │
│   ┌─────────────────────┐  ┌───────────────────────────────┐ │
│   │  Datadog MCP        │  │  SystemIntelligence MCP       │ │
│   │  (separate server)  │  │  (your code)                  │ │
│   │                     │  │                               │ │
│   │  • search_errors    │  │  • search_source_code         │ │
│   │  • get_traces       │  │  • get_file_content           │ │
│   │  • get_trace_detail │  │  • list_repositories          │ │
│   │  • list_services    │  │  • query_db_logs (Tier 2)     │ │
│   │                     │  │  • create_bug (Tier 2)        │ │
│   │                     │  │  • create_story (Tier 2)      │ │
│   └─────────────────────┘  └───────────────────────────────┘ │
└──────────────────────────────────────────────────────────────┘
```

### What Copilot replaces

| Dropped Component              | Replacement              |
|---------------------------------|--------------------------|
| `AiFoundryAnalyzer`             | Copilot's own LLM        |
| `DirectApiToolExecutor`         | MCP tool attributes       |
| `IToolExecutor` interface       | MCP SDK tool registration |
| `Investigate.razor` chat UI     | Copilot Chat window       |
| `AiFoundryOptions` / Azure OpenAI config | N/A            |

### What remains in SystemIntelligence

| Kept Component                  | Purpose                  |
|---------------------------------|--------------------------|
| `ISourceCodeReader` / `LocalFileSystemReader` | Source code access |
| `IDbLogCollector` (Tier 2)      | DB log queries           |
| `IWorkItemClient` (Tier 2)      | Azure DevOps work items  |
| `EfInvestigationStore`          | Audit / history logging  |
| Auth plumbing (Tier 1)          | Entra ID / PAT validation|

### Why Datadog is separate

Datadog tools (`search_errors`, `get_traces`, `get_trace_details`, `list_services`) are delegated to a **separate MCP server** — either an open-source community server or a Datadog-provided one. This keeps SystemIntelligence focused on what you own: source code, logs, and work items.

---

## Self-Contained Server Design

The entire system — MCP server, embedding model, vector store, git client — runs in a **single Docker container** with **no external AI service dependencies**.

```
┌─────────────────────────────────────────────────────────────┐
│              SystemIntelligence MCP Server                   │
│              (single Docker container)                       │
│                                                             │
│  ┌─────────────────────┐   ┌─────────────────────────────┐  │
│  │  MCP Transport      │   │  Repo Sync Service          │  │
│  │  (stdio or SSE)     │   │  (git clone/pull on start)  │  │
│  └────────┬────────────┘   └──────────┬──────────────────┘  │
│           │                           │                     │
│  ┌────────▼────────────────────────────▼──────────────────┐  │
│  │                    Tool Layer                          │  │
│  │  • search_source_code  (vector + text hybrid)         │  │
│  │  • get_file_content    (read from cloned repos)       │  │
│  │  • list_repositories   (enumerate cloned repos)       │  │
│  │  • query_db_logs       (Tier 2)                       │  │
│  │  • create_bug / story  (Tier 2)                       │  │
│  └────────┬───────────────────────────┬──────────────────┘  │
│           │                           │                     │
│  ┌────────▼────────────┐   ┌──────────▼──────────────────┐  │
│  │  Embedding Engine   │   │  Vector Store               │  │
│  │  ONNX Runtime       │   │  SQLite + sqlite-vec        │  │
│  │  all-MiniLM-L6-v2   │   │  (persisted to volume)     │  │
│  │  (80 MB, CPU-only)  │   │                            │  │
│  └─────────────────────┘   └────────────────────────────┘  │
│                                                             │
│  Auth: Entra token / PAT passed per-request from client     │
└─────────────────────────────────────────────────────────────┘
```

---

## Component Details

### 1. Embedding Model — ONNX Runtime (in-process, no GPU)

The model `all-MiniLM-L6-v2` is a widely-used sentence transformer that produces 384-dimensional vectors. It runs via ONNX Runtime directly in the C# process:

```csharp
var session = new InferenceSession("models/all-MiniLM-L6-v2.onnx");
// Tokenize input, run inference, get 384-dim float[] vector
```

| Property     | Value                      |
|-------------|----------------------------|
| Model size  | ~80 MB                     |
| Dimensions  | 384                        |
| Latency     | ~5-10ms per embedding (CPU)|
| Dependencies| None — runs in-process     |

No Azure OpenAI calls, no API keys for embeddings — fully local.

### 2. Vector Store — SQLite + sqlite-vec

[sqlite-vec](https://github.com/asg017/sqlite-vec) is a SQLite extension for vector similarity search. Single file, zero infrastructure:

```sql
CREATE VIRTUAL TABLE source_chunks USING vec0(
    embedding float[384],
    +repository TEXT,
    +file_path TEXT,
    +chunk_index INTEGER,
    +content TEXT
);

-- Semantic search: 10 nearest chunks to query vector
SELECT rowid, distance, repository, file_path, chunk_index, content
FROM source_chunks
WHERE embedding MATCH ?query_vector
ORDER BY distance
LIMIT 10;
```

| Property     | Value                                    |
|-------------|------------------------------------------|
| Storage     | Single `.db` file on mounted volume      |
| Capacity    | Millions of vectors (~50-100K chunks for 20 repos) |
| Persistence | Survives container restarts via volume   |
| Dependencies| None — embedded in-process               |

### 3. Repository Management

The MCP server supports two modes for discovering which repositories to index. Both can be used together.

#### Mode A: Admin Endpoint (Dynamic)

Repositories are managed at runtime via MCP admin tools (or a REST endpoint on the SSE transport). The server maintains a `repositories` table in its SQLite database:

```sql
CREATE TABLE repositories (
    id          INTEGER PRIMARY KEY,
    name        TEXT NOT NULL UNIQUE,
    url         TEXT NOT NULL,           -- https://dev.azure.com/Org/Project/_git/Repo
    branch      TEXT NOT NULL DEFAULT 'main',
    enabled     BOOLEAN NOT NULL DEFAULT 1,
    last_sha    TEXT,                     -- last indexed commit SHA
    last_synced TEXT,                     -- ISO 8601 timestamp
    added_by    TEXT                      -- user who added it
);
```

MCP admin tools:

```csharp
[McpServerToolType]
public class AdminTools(IRepoRegistry registry)
{
    [McpServerTool, Description("Add a repository to be indexed by the MCP server")]
    public async Task<string> AddRepository(
        [Description("Display name (e.g. 'InventoryService')")] string name,
        [Description("Git clone URL")] string url,
        [Description("Branch to track (default: main)")] string branch = "main")
    { ... }

    [McpServerTool, Description("Remove a repository from the index")]
    public async Task<string> RemoveRepository(string name)
    { ... }

    [McpServerTool, Description("List all indexed repositories and their sync status")]
    public async Task<string> ListIndexedRepositories()
    { ... }

    [McpServerTool, Description("Force re-index a specific repository or all repositories")]
    public async Task<string> Reindex(string? repository = null)
    { ... }
}
```

Workflow:

```
User (via Copilot):  "Add the InventoryService repo"
    │
    ├─ Copilot calls → AddRepository("InventoryService",
    │    "https://dev.azure.com/HiltonGrandVacations/OneVision/_git/InventoryService")
    │
    ├─ Server: inserts row into repositories table
    ├─ Server: git clone --depth 1 → /data/repos/InventoryService
    ├─ Server: chunks + embeds source files → sqlite-vec
    └─ Returns: "InventoryService indexed (342 files, 1,847 chunks)"
```

#### Mode B: Git Submodules (Static / Mono-Repo)

Point the MCP server at a mono-repo that uses `.gitmodules` (like the Tex project). The server reads `.gitmodules`, runs `git submodule update --init --recursive`, and indexes all submodule working trees.

```json
{
  "SourceCode": {
    "Mode": "submodules",
    "RootPath": "/data/repos/Tex",
    "MonoRepoUrl": "https://dev.azure.com/HiltonGrandVacations/OneVision/_git/Tex"
  }
}
```

The Tex project already defines 40+ submodules across Azure DevOps orgs. The MCP server would:

1. Clone the Tex mono-repo (shallow)
2. Parse `.gitmodules` to discover all submodules
3. Init + update submodules (depth 1)
4. Index each submodule as a separate "repository" in the search index

Adding/removing repos = updating `.gitmodules` in the Tex repo and pushing. The MCP server picks up changes on next sync.

#### Comparison

| Aspect          | Admin Endpoint (Dynamic)        | Submodules (Static)              |
|-----------------|---------------------------------|----------------------------------|
| Add a repo      | MCP tool call at runtime        | Edit `.gitmodules`, push, re-sync|
| Config stored in| SQLite `repositories` table     | `.gitmodules` in git             |
| Version control | No (runtime state)              | Yes (tracked in git)             |
| Multi-user      | Each user can add their own     | Shared, consistent across users  |
| Best for        | Ad-hoc investigation, one-off repos | Team-wide standard repo set  |

#### Hybrid: Both Together

Use submodules as the **baseline** (team's standard list of repos) and admin endpoints for **ad-hoc additions** (temporarily index a repo for a specific investigation):

```
┌──────────────────────────────────────────┐
│          Repository Sources              │
│                                          │
│  .gitmodules (baseline)                  │
│  ├─ InventoryService                     │
│  ├─ OrdersService                        │
│  ├─ contractsService                     │
│  ├─ ... (40+ repos)                      │
│                                          │
│  Admin-added (ad-hoc)                    │
│  ├─ some-vendor-sdk (temporary)          │
│  └─ legacy-billing (one-off debug)       │
│                                          │
│  Both feed into the same index pipeline  │
└──────────────────────────────────────────┘
```

### 4. Indexing Pipeline — On Startup + Incremental

```
Container starts
    │
    ├─ 1. Load repo list
    │     Read .gitmodules (if submodule mode)
    │     Read repositories table (if admin mode)
    │     Merge both lists, deduplicate by name
    │
    ├─ 2. Clone/pull repos (using PAT from config or env var)
    │     New repos: git clone --depth 1
    │     Existing repos: git pull --ff-only
    │     Submodules: git submodule update --init --recursive
    │
    ├─ 3. Diff against last indexed commit SHA (stored in SQLite)
    │     Only re-index changed files
    │
    ├─ 4. Chunk changed source files
    │     Split by function/class boundaries (tree-sitter) or fixed-size chunks
    │     Each chunk: ~500-1000 tokens with overlap
    │
    ├─ 5. Embed chunks via ONNX model
    │     Batch process, ~5ms per chunk
    │
    └─ 6. Upsert into sqlite-vec
          Delete old chunks for changed files, insert new ones
          Update last_sha in repositories table
```

| Scenario     | Duration                                 |
|-------------|------------------------------------------|
| Cold start  | 2-5 minutes (first time, all repos)      |
| Warm start  | Seconds (incremental, changed files only)|

### 5. Change Monitoring

The server monitors indexed repos for changes and re-indexes automatically:

| Trigger              | How                                        |
|----------------------|--------------------------------------------|
| **Polling**          | Background timer every N minutes: `git fetch --dry-run` to check for new commits |
| **On-demand**        | Admin `Reindex` tool call                  |
| **Webhook** (future) | Azure DevOps service hook POSTs to `/webhook/repo-updated` on push |

When changes are detected:

```
Poller detects new commits on InventoryService (main)
    │
    ├─ git pull --ff-only
    ├─ git diff --name-only old_sha..new_sha → list of changed files
    ├─ Delete old chunks for changed files
    ├─ Chunk + embed changed files
    └─ Upsert into sqlite-vec, update last_sha
```

Only changed files are re-processed — the index stays warm.

### 4. Hybrid Search — Vector + Text

The `search_source_code` tool performs both semantic and text search:

```csharp
[McpServerTool, Description("Search source code across repositories")]
public async Task<string> SearchSourceCode(string query, string? repository = null)
{
    // 1. Vector search (semantic) — "find retry logic for HTTP calls"
    var queryVector = _embedder.Embed(query);
    var semanticResults = await _vectorStore.SearchAsync(queryVector, repository, limit: 10);

    // 2. Text search (exact) — "DatadogClient" finds exact symbol names
    var textResults = await _gitGrep.SearchAsync(query, repository, limit: 10);

    // 3. Merge, deduplicate, rank
    var merged = MergeResults(semanticResults, textResults);
    return FormatResults(merged);
}
```

| Query Type             | Engine         | Example                           |
|------------------------|----------------|-----------------------------------|
| Exact text / symbol    | `git grep`     | "Find all uses of `DatadogClient`"|
| Semantic / natural lang| sqlite-vec     | "Find the error retry logic"      |
| Structural (future)    | tree-sitter/GitNexus | "What calls this method?"    |

---

## MCP Tool Definitions

### Tier 1 (initial)

```csharp
[McpServerToolType]
public class SourceCodeTools(ISourceCodeReader reader)
{
    [McpServerTool, Description("Search source code across all repositories using hybrid text + semantic search")]
    public async Task<string> SearchSourceCode(
        [Description("Search keyword or natural language query")] string query,
        [Description("Optional repository name to scope the search")] string? repository = null)
    { ... }

    [McpServerTool, Description("Read the content of a specific source file")]
    public async Task<string> GetFileContent(
        [Description("Repository name")] string repository,
        [Description("File path within the repository")] string path)
    { ... }

    [McpServerTool, Description("List all available repositories")]
    public async Task<string> ListRepositories()
    { ... }
}
```

### Admin Tools (both tiers)

```csharp
[McpServerToolType]
public class AdminTools(IRepoRegistry registry)
{
    [McpServerTool, Description("Add a git repository to be indexed")]
    public async Task<string> AddRepository(
        [Description("Display name")] string name,
        [Description("Git clone URL")] string url,
        [Description("Branch to track (default: main)")] string branch = "main")
    { ... }

    [McpServerTool, Description("Remove a repository from the index")]
    public async Task<string> RemoveRepository(string name)
    { ... }

    [McpServerTool, Description("List all indexed repositories and their sync status")]
    public async Task<string> ListIndexedRepositories()
    { ... }

    [McpServerTool, Description("Force re-index a specific repository or all repositories")]
    public async Task<string> Reindex(string? repository = null)
    { ... }
}
```

### Tier 2 (future)

```csharp
[McpServerToolType]
public class DbLogTools(IDbLogCollector logs)
{
    [McpServerTool, Description("Query database logs by time range, host, level, or message content")]
    public async Task<string> QueryDbLogs(
        [Description("Start time (ISO 8601)")] string from,
        [Description("End time (ISO 8601)")] string to,
        [Description("Optional log level filter (Error, Warning, Info)")] string? level = null,
        [Description("Optional message text filter")] string? messageFilter = null)
    { ... }
}

[McpServerToolType]
public class WorkItemTools(IWorkItemClient workItems)
{
    [McpServerTool, Description("Create a bug work item in Azure DevOps")]
    public async Task<string> CreateBug(
        [Description("Bug title")] string title,
        [Description("Bug description with repro steps")] string description,
        [Description("Optional area path")] string? areaPath = null)
    { ... }

    [McpServerTool, Description("Create a user story in Azure DevOps")]
    public async Task<string> CreateUserStory(
        [Description("Story title")] string title,
        [Description("Acceptance criteria and description")] string description,
        [Description("Optional area path")] string? areaPath = null)
    { ... }
}
```

---

## Security

### Token passing from client

**Option A: Environment variables (single-user / local)**

```json
{
  "servers": {
    "system-intelligence": {
      "type": "stdio",
      "command": "docker",
      "args": ["run", "-i", "--rm",
        "-e", "AZURE_DEVOPS_PAT=${AZURE_DEVOPS_PAT}",
        "-v", "si-data:/data",
        "systemintelligence-mcp:latest"
      ]
    }
  }
}
```

**Option B: SSE transport with auth header (shared / deployed)**

```json
{
  "servers": {
    "system-intelligence": {
      "type": "sse",
      "url": "https://si-mcp.azurecontainerapps.io/mcp",
      "headers": {
        "Authorization": "Bearer ${ENTRA_TOKEN}"
      }
    }
  }
}
```

The MCP server validates the Entra token, extracts user identity, and uses the associated PAT (or on-behalf-of flow) to access Azure DevOps repos.

---

## Deployment Options

| Mode              | Transport | Vector DB                        | Config                 |
|-------------------|-----------|----------------------------------|------------------------|
| Local (dotnet)    | stdio     | SQLite file in project dir       | `.vscode/mcp.json`     |
| Local (container) | stdio     | SQLite on mounted volume         | `.vscode/mcp.json`     |
| Azure Container App | SSE/HTTP | SQLite on persistent volume or Azure AI Search | Copilot / client config |

The **same Docker image** works in all modes. The only differences are the transport (stdio vs SSE) and how credentials arrive.

### Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
RUN apt-get update && apt-get install -y git
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/SystemIntelligence.Mcp -c Release -o /app

FROM base
COPY --from=build /app .
COPY models/all-MiniLM-L6-v2.onnx /app/models/
VOLUME /data
ENTRYPOINT ["dotnet", "SystemIntelligence.Mcp.dll"]
```

---

## NuGet Packages

| Package                          | Purpose                              |
|----------------------------------|--------------------------------------|
| `ModelContextProtocol`           | MCP server SDK (stdio + SSE)         |
| `Microsoft.ML.OnnxRuntime`       | Run embedding model in-process       |
| `Microsoft.Data.Sqlite`          | SQLite access                        |
| `sqlite-vec` (native lib)        | Vector search extension for SQLite   |
| `LibGit2Sharp` (optional)        | Git operations without shelling out  |

---

## Project Structure (proposed)

```
SystemIntelligence/
├── src/
│   ├── SystemIntelligence.Core/        # Existing — interfaces, models, services
│   ├── SystemIntelligence.Web/         # Existing — Blazor UI (optional, keep for browsing)
│   └── SystemIntelligence.Mcp/         # NEW — MCP server entry point
│       ├── Program.cs                  # MCP host setup, DI, transport config
│       ├── Tools/
│       │   ├── SourceCodeTools.cs      # search_source_code, get_file_content, list_repositories
│       │   ├── AdminTools.cs           # add_repository, remove_repository, reindex
│       │   ├── DbLogTools.cs           # query_db_logs (Tier 2)
│       │   └── WorkItemTools.cs        # create_bug, create_story (Tier 2)
│       ├── Repos/
│       │   ├── IRepoRegistry.cs         # Add/remove/list repos interface
│       │   ├── SqliteRepoRegistry.cs    # SQLite-backed repo registry
│       │   ├── SubmoduleRepoSource.cs   # Reads .gitmodules to discover repos
│       │   ├── RepoSyncService.cs       # Git clone/pull + submodule update
│       │   └── ChangeMonitor.cs         # Background polling for new commits
│       ├── Search/
│       │   ├── OnnxEmbedder.cs          # ONNX Runtime embedding wrapper
│       │   ├── VectorStore.cs           # sqlite-vec read/write
│       │   ├── HybridSearchService.cs   # Combines vector + git grep
│       │   └── IndexingService.cs       # Chunk + embed + upsert pipeline
│       └── SystemIntelligence.Mcp.csproj
├── models/
│   └── all-MiniLM-L6-v2.onnx          # Embedding model (~80 MB)
├── tests/
│   └── SystemIntelligence.Tests/       # Existing + new MCP tool tests
└── SystemIntelligence.sln
```

---

## Migration Path

| Phase   | What changes                                               |
|---------|-------------------------------------------------------------|
| Phase 1 | Create `SystemIntelligence.Mcp` project with source code tools + `git grep` search |
| Phase 2 | Add repo management (admin tools + submodule discovery + change monitor) |
| Phase 3 | Add ONNX embedder + sqlite-vec for hybrid semantic search  |
| Phase 4 | Add `query_db_logs` tool (when `IDbLogCollector` is implemented) |
| Phase 5 | Add `create_bug` / `create_story` tools (when `IWorkItemClient` is implemented) |
| Phase 6 | Deploy to Azure Container App with SSE transport + Entra auth |
