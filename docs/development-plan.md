# HgvMate MCP Server — Development Plan

> Self-contained .NET 10 MCP server providing AI agents with source code search, structural code intelligence, file access, and repository management.

## Overview

HgvMate is an MCP (Model Context Protocol) server that gives VS Code Copilot Chat deep access to your organization's source code. Repos are managed via admin tool calls, cloned to a persistent `/data` volume, and triple-indexed:

1. **Text** — git grep for exact-match / regex search
2. **Semantic** — ONNX embeddings (all-MiniLM-L6-v2) + sqlite-vec for "find code that does X" queries
3. **Structural** — GitNexus (tree-sitter AST parsing + graph DB) for call chains, blast radius, and symbol references

All 11 MCP tools are prefixed `hgvmate_*`. Supports both stdio and SSE transports. Runs locally via `dotnet run`, in a Docker container, or on Proxmox. GitNexus is embedded in the container and orchestrated transparently — users see only HgvMate tools.

The ONNX model is an **encoder** (text → 384-dim vector), not an LLM. Copilot (remote) provides all reasoning. GitNexus is fully deterministic — no AI/ML.

## Architecture Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Repo management | Admin tool calls (add/remove/reindex) | Dynamic, no coupling to Tex workspace or submodules |
| Transport | Both stdio + SSE (config-driven) | stdio for local dev, SSE for remote/Proxmox |
| Tool naming | `hgvmate_*` prefix | Avoid collisions with other MCP servers |
| Git credentials | Per-source PAT | Each repo source (GitHub, Azure DevOps) uses its own PAT |
| Search layers | git grep + ONNX/sqlite-vec + GitNexus | Text, semantic, and structural — each answers different questions |
| Persistence | Single `/data` volume with JSON metadata + binary vectors | One mount point, survives container updates, no external DB |
| Structural analysis | GitNexus embedded in container (Option B) | Transparent orchestration, no separate deployment |
| Docker base | Ubuntu 24.04 | Supports tree-sitter native build tools required by GitNexus |
| Test framework | MSTest with parallel execution at method level | Fast CI, no Docker/volume needed |
| PR/repo | Do not submit PR until instructed | Project lives in Tex workspace temporarily; will move to own repo |
| GitNexus scheduling | Fire-and-forget via bounded `Channel<string>` | ONNX (CPU-bound) and GitNexus (I/O-bound) overlap without contention |
| Bulk VectorStore saves | `deferSave: true` in `IndexRepoAsync` during bulk | Avoids writing ~300 MB after every repo; single flush at end of `SyncAllAsync` |
| Parallel repo sync | `Parallel.ForEachAsync(maxDegreeOfParallelism: 3)` | Replaces sequential `foreach`; semaphore already existed for gating |
| GitNexus skip for doc changes | `HasStructuralChanges()` filter | Avoids re-analysis for `.md`/`.json`/`.yaml` incremental changes |

## Sync Pipeline Design

The sync pipeline is optimized to overlap complementary workloads and avoid redundant I/O:

### GitNexus: Fire-and-forget
`RepoSyncService` enqueues repo names into a bounded `Channel<string>` instead of `await`-ing `AnalyzeAsync` inline. A dedicated `RunGitNexusWorkerAsync` background loop drains the queue with up to 3 concurrent analyses. This lets ONNX embedding (600%+ CPU) and GitNexus analysis (5-15% CPU, I/O-bound) run concurrently, cutting wall time by ~60% for bulk syncs.

### Batch VectorStore saves
During `SyncAllAsync`, each `IndexRepoAsync` call passes `deferSave: true` — vectors accumulate in the in-memory `ConcurrentDictionary` but the ~300 MB binary file is written only once after all repos finish. Incremental single-repo syncs still save immediately (low overhead, already deferred per file).

### Parallel clone + embed
`SyncAllAsync` uses `Parallel.ForEachAsync` with `maxDegreeOfParallelism: 3` instead of a sequential `foreach`. The `SemaphoreSlim(3,3)` gate continues to protect individual `SyncRepoAsync` calls from unbounded concurrency.

### Skip GitNexus for non-structural incremental changes
When an incremental sync detects changed files, `HasStructuralChanges()` checks if any are AST-parseable (`.cs`, `.ts`, `.js`, `.py`, `.java`, etc.). If only docs/config changed, GitNexus re-analysis is skipped entirely — a useful optimisation for repos with frequent README/changelog commits.

## Volume Architecture

```
/data/                              ← single mount point (volume or local folder)
├── repo-meta/                      ← JSON files for repo metadata (one per repo)
│   ├── InventoryService.json
│   └── OrdersService.json
├── vectors.bin                     ← binary vector store for embeddings
├── repos/                          ← cloned repositories
│   ├── InventoryService/
│   │   ├── .git/
│   │   ├── .gitnexus/              ← GitNexus index (per-repo)
│   │   └── src/...
│   ├── OrdersService/
│   └── ...
└── logs/                           ← optional app logs
```

| Data | Location | In image or volume? |
|------|----------|---------------------|
| App binary + ONNX model | `/app/` | Image (replaced on update) |
| Repo registry | `/data/repo-meta/*.json` | Volume (persists) |
| Vector index (embeddings) | `/data/vectors.bin` | Volume (persists) |
| Cloned repos + GitNexus indexes | `/data/repos/` | Volume (persists) |
| App config defaults | `appsettings.json` | Image (overridden by env vars) |

## Deployment Modes

### Mode 1: Local development (`dotnet run`)

- Data at `./data` (configurable via `HGVMATE_DATA_PATH` env var)
- `PollIntervalMinutes: 0` disables background polling — manual `hgvmate_reindex`
- Tests use in-memory SQLite — no file system dependency, pipeline-safe

### Mode 2: Local Docker container

```bash
docker run -i --rm -v hgvmate-data:/data -e AZURE_DEVOPS_PAT=... hgvmate:latest
```

- Named volume `hgvmate-data` persists across container rebuilds
- Copilot connects via stdio in `.vscode/mcp.json`

### Mode 3: Proxmox (remote)

```bash
docker compose -f docker-compose.proxmox.yml up -d
```

- SSE transport, HTTP endpoint
- Bind-mount `/opt/hgvmate/data` at `/data` for persistence
- Aspire Dashboard sidecar for telemetry
- PAT-based git credentials via environment variables

## Implementation Phases

### Phase 1: Core Infrastructure ✅

*Foundation — everything else depends on this. **Completed.***

| Step | Description |
|------|-------------|
| 1 | **NuGet packages** — `ModelContextProtocol`, `Microsoft.ML.OnnxRuntime`, `Microsoft.Extensions.Hosting` |
| 2 | **Configuration model** — `HgvMateOptions` (DataPath, Transport), `RepoSyncOptions` (PollIntervalMinutes, ClonePath), `SearchOptions`, `CredentialOptions` in `Configuration/` |
| 3 | **Program.cs** — `HostApplicationBuilder` with MCP server, DI, hosted services. Resolve DataPath from env var → config → `./data`. Ensure directories exist. Transport selection from config |
| 4 | **Git credential provider** — `GitCredentialProvider`: resolves credentials per repo source. For `github` repos, uses `GITHUB_TOKEN`. For `azuredevops` repos, uses `AZURE_DEVOPS_PAT`. Injects auth into git clone/pull commands |
| 5 | **JSON-based repo registry** — Repo metadata stored as individual JSON files under `/data/repo-meta/` (migrated from SQLite for network filesystem compatibility) |

### Phase 2: Repository Management ✅

*Depends on Phase 1. **Completed.***

| Step | Description |
|------|-------------|
| 6 | **Repo metadata** — JSON files with id, name, url, branch, source, enabled, last_sha, last_synced, added_by, sync_state, last_error fields |
| 7 | **IRepoRegistry + JsonRepoRegistry** — `AddAsync`, `RemoveAsync`, `GetAllAsync`, `GetByNameAsync`, `UpdateLastShaAsync` with async file I/O |
| 8 | **RepoSyncService** — `BackgroundService`: on startup reads registry, clones missing repos (`--depth 1 --single-branch`), pulls existing repos. Uses `GitCredentialProvider`. Parallel sync with semaphore gating, retry logic for transient errors |
| 9 | **Admin MCP tools** (class: `AdminTools`) |
|   | — `hgvmate_add_repository(name, url, branch?, source?)`: insert into registry, trigger clone + index |
|   | — `hgvmate_remove_repository(name)`: remove from registry, delete clone directory |
|   | — `hgvmate_list_repositories()`: all repos with sync status, last SHA, last synced time |
|   | — `hgvmate_reindex(repository?)`: trigger immediate sync + re-index for one or all repos |
|   | — `hgvmate_index_status(repository?)`: per-repo status showing clone state, last SHA, and sync state |

### Phase 3: Source Code Access (Text) ✅

*Depends on Phase 2. **Completed.***

| Step | Description |
|------|-------------|
| 10 | **SourceCodeReader** — reads from cloned repos. Methods: `GetFileAsync`, `ListDirectoryAsync`. Path traversal protection + file size limits |
| 11 | **GitGrepSearchService** — shells out to `git grep -rli` across enabled repos. Supports optional repo filter. Returns file paths + matching lines |
| 12 | **Source MCP tools** (class: `SourceCodeTools`) |
|    | — `hgvmate_search_source_code(query, repository?)`: hybrid search (text + semantic) |
|    | — `hgvmate_get_file_content(repository, path)`: read file from cloned repo |

### Phase 4: GitNexus Integration (Structural) ✅

*Depends on Phase 2. **Completed.***

| Step | Description |
|------|-------------|
| 13 | **Node.js + GitNexus in Docker** — Node.js 22 + `gitnexus` installed globally in Dockerfile |
| 14 | **GitNexusService** — `AnalyzeAsync(repoName)` runs `gitnexus analyze`. Enqueued via bounded `Channel<string>` for concurrent background analysis |
| 15 | **Structural MCP tools** (class: `StructuralTools`) |
|    | — `hgvmate_find_symbol(name, repository?)`: 360-degree symbol view |
|    | — `hgvmate_get_references(name, repository?)`: what calls/uses this symbol |
|    | — `hgvmate_get_call_chain(name, repository?)`: execution flow trace |
|    | — `hgvmate_get_impact(name, repository?)`: blast radius |

### Phase 5: Vector Search (Semantic) ✅

*Depends on Phase 2. **Completed.***

| Step | Description |
|------|-------------|
| 17 | **OnnxEmbedder** — wraps `Microsoft.ML.OnnxRuntime` with all-MiniLM-L6-v2 (384-dim). Auto-selects quantized INT8 models per architecture. CUDA/OpenVINO/CPU provider auto-detection |
| 18 | **VectorStore** — binary-file-backed cache (`vectors.bin`) with SIMD-accelerated cosine similarity via `TensorPrimitives`. Atomic re-index (index-then-swap) prevents data gaps |
| 19 | **IndexingService** — chunks source files (~800 tokens, overlap), embeds via ONNX, upserts into binary store. Batch ONNX inference for efficiency. File size limits prevent OOM |
| 20 | **HybridSearchService** — runs vector search + git grep in parallel, merges/deduplicates, ranks |

### Phase 6: Change Monitoring ✅

*Depends on Phases 2–5. **Completed.***

| Step | Description |
|------|-------------|
| 21 | **Poll loop** — `RepoSyncService`: periodic `git fetch` per repo, compare remote HEAD vs stored `last_sha`. Incremental re-index for changed files only. GitNexus re-analysis only for structural file changes |
| 22 | **On-demand reindex** — `hgvmate_reindex` triggers immediate sync + full re-index |

### Phase 7: Packaging & Testing ✅

*Depends on all above. **Completed.***

| Step | Description |
|------|-------------|
| 23 | **Dockerfile** — Multi-stage: .NET SDK build → Ubuntu 24.04 runtime + Node.js 22 + git + ONNX models. Self-contained publish. OpenVINO support for x86_64, standard ORT for ARM64 |
| 24 | **REST API** — Full REST API with health endpoint, search, repository management, structural analysis, rate limiting |
| 25 | **Docker Compose** — Standard dev compose + Proxmox deployment with Aspire Dashboard, healthchecks |
| 26 | **Telemetry** — OpenTelemetry integration with HVO.Enterprise.Telemetry, custom metrics and activities |
| 27 | **MSTest unit tests** — JsonRepoRegistry, VectorStore, IndexingService, AdminTools, SourceCodeTools, OnnxEmbedder, configuration, credential provider |
| 28 | **Integration + Docker tests** — SSE transport, REST API, Docker container validation |

## Dependency Map

```
Phase 1 (Core Infrastructure)
    │
    ▼
Phase 2 (Repo Management)
    │
    ├───────────────┬───────────────┐
    ▼               ▼               ▼
Phase 3          Phase 4         Phase 5
(Text Search)   (GitNexus)      (Vector Search)
    │               │               │
    └───────────────┴───────────────┘
                    │
                    ▼
              Phase 6 (Change Monitoring)
                    │
                    ▼
              Phase 7 (Packaging & Testing)
```

Phases 3, 4, 5 are independent — they all just need repos cloned (Phase 2).

## MCP Tools Summary (11 tools)

| Tool | Phase | Backend | Description |
|------|-------|---------|-------------|
| `hgvmate_add_repository` | 2 | SqliteRepoRegistry | Add a repo to be indexed (source: github or azuredevops) |
| `hgvmate_remove_repository` | 2 | SqliteRepoRegistry | Remove a repo and its data |
| `hgvmate_list_repositories` | 2 | SqliteRepoRegistry | List repos with sync status |
| `hgvmate_reindex` | 2+6 | RepoSyncService | Force sync + re-index |
| `hgvmate_index_status` | 2 | RepoSyncService | Per-repo index status: clone/text/vector/GitNexus readiness |
| `hgvmate_search_source_code` | 3→5 | HybridSearchService | Text + semantic search (text only until Phase 5) |
| `hgvmate_get_file_content` | 3 | SourceCodeReader | Read a source file |
| `hgvmate_find_symbol` | 4 | GitNexus (context) | Symbol view: class/method + callers + hierarchy |
| `hgvmate_get_references` | 4 | GitNexus (context) | What calls/uses this symbol |
| `hgvmate_get_call_chain` | 4 | GitNexus (query) | Execution flow trace |
| `hgvmate_get_impact` | 4 | GitNexus (impact) | Blast radius with depth/confidence |

## Project Structure

```
HgvMate/
├── HgvMate.slnx
├── README.md
├── dockerfile
├── .gitignore
├── appsettings.json
├── appsettings.Development.json
├── docs/
│   └── development-plan.md          ← this file
├── src/HgvMate.Mcp/
│   ├── HgvMate.Mcp.csproj
│   ├── Program.cs
│   ├── Configuration/
│   │   ├── HgvMateOptions.cs
│   │   ├── RepoSyncOptions.cs
│   │   ├── SearchOptions.cs
│   │   ├── CredentialOptions.cs
│   │   └── RepoSource.cs              ← enum: GitHub, AzureDevOps
│   ├── Tools/
│   │   ├── AdminTools.cs
│   │   ├── SourceCodeTools.cs
│   │   └── StructuralTools.cs
│   ├── Repos/
│   │   ├── IRepoRegistry.cs
│   │   ├── JsonRepoRegistry.cs
│   │   ├── RepoSyncService.cs
│   │   └── GitCredentialProvider.cs
│   └── Search/
│       ├── OnnxEmbedder.cs
│       ├── VectorStore.cs
│       ├── IndexingService.cs
│       ├── HybridSearchService.cs
│       ├── GitGrepSearchService.cs
│       ├── GitNexusService.cs
│       └── SourceCodeReader.cs
└── tests/HgvMate.Tests/
    ├── HgvMate.Tests.csproj
    └── ...
```

## Verification Checklist

1. `dotnet build` — solution compiles clean
2. `dotnet test` — all unit tests pass (in-memory SQLite, mocked services)
3. Local stdio — `dotnet run`, configure `.vscode/mcp.json`, Copilot lists tools and calls `hgvmate_list_repositories`
4. Add repo E2E — `hgvmate_add_repository` → clone completes → `hgvmate_search_source_code` returns results
5. GitNexus E2E — after repo indexed, `hgvmate_find_symbol` returns callers/hierarchy
6. Docker build — `docker build -t hgvmate .` succeeds
7. Docker run — `docker run -i -v hgvmate-data:/data` starts, responds to MCP handshake
8. Container update — rebuild image, re-run with same volume → data persists, incremental re-index only
9. SSE transport — start with `HGVMATE_TRANSPORT=sse`, HTTP endpoint responds at `/mcp`

## Scope Boundaries

**Included (Tier 1):**

- Source code search (text + semantic + structural), file read
- Admin repo management (add/remove/reindex/status)
- GitNexus integration (find symbol, get references, call chain, impact)
- Background repo sync with incremental change monitoring
- ONNX embeddings + sqlite-vec vector search
- Both stdio and SSE transports
- PAT credential support per repo source
- Persistent volume architecture (survives container updates)
- Docker packaging (Ubuntu 24.04, multi-stage with Node.js + .NET)
- Local dev mode (`dotnet run` with local `./data` folder)

**Excluded (future tiers):**

- DB log queries (`hgvmate_query_db_logs`)
- Azure DevOps work items (`hgvmate_create_bug`, `hgvmate_create_story`)
- Webhook-based change detection (Azure DevOps service hooks)
- Cross-repo interaction tracing (runtime/Datadog needed)

## Notes

- **GitNexus multi-repo:** Each repo is indexed independently. Cross-service interactions (HTTP calls between services) can't be traced structurally — that requires runtime data (Datadog traces). Could be a future MCP integration point.
- **Repo migration:** Project currently lives in `Tex/projects/HgvMate/` for development convenience. Will move to its own repo when ready — keep dependencies self-contained to make this easy.
