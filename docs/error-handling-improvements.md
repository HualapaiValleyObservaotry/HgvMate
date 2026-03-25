# Error Handling, Logging & Resilience Improvements

Tracked issues discovered during bulk repo onboarding (39 repos to Azure ACI).  
Root cause: adding repos rapidly caused HTTP 500 errors with no feedback, no retry, and no persistent error state.

---

## 1. Fire-and-Forget Sync — No Error Capture

**Problem:** All sync operations use `_ = Task.Run(() => syncService.SyncRepoAsync(repo))` — errors are logged to console but never surfaced to the caller, never persisted, and never retried.

**Affected code:**
- [ApiEndpoints.cs](src/HgvMate.Mcp/Api/ApiEndpoints.cs) — POST `/api/repositories`, POST `/{name}/reindex`, POST `/reindex`
- [AdminTools.cs](src/HgvMate.Mcp/Tools/AdminTools.cs) — `hgvmate_add_repository`, `hgvmate_reindex`

**Impact:** User gets `201 Created` but sync silently fails. No way to distinguish "syncing" from "sync failed" from "never synced".

**Fix:**
- Track background sync tasks with a `ConcurrentDictionary<string, SyncStatus>` in `RepoSyncService`
- Expose sync status (queued / syncing / completed / failed + error message) via `/api/repositories/{name}/status`
- Consider returning `202 Accepted` instead of `201 Created` to signal async processing

---

## 2. No Retry on Transient Git Failures

**Problem:** `RepoSyncService.SyncRepoAsync` catches exceptions and logs them, but never retries. Network blips, temporary auth issues, and Azure DevOps rate limits all result in permanent failure.

**Affected code:**
- [RepoSyncService.cs](src/HgvMate.Mcp/Repos/RepoSyncService.cs) — `SyncRepoAsync`, `CloneRepoAsync`, `PullRepoAsync`

**Impact:** During bulk onboarding, 4 of 39 repos got HTTP 500 and required manual retry.

**Fix:**
- Add exponential backoff retry (3 attempts, 2s/4s/8s delay) around `CloneRepoAsync` and `PullRepoAsync`
- Distinguish transient errors (network, 429 rate limit, auth timeout) from permanent errors (404 repo not found, access denied)
- Only retry transient errors

---

## 3. No Persistent Error State in Database

**Problem:** The `repositories` table only tracks `last_sha` and `last_synced`. There is no column for error messages, failure counts, or sync-in-progress state.

**Affected code:**
- [SqliteRepoRegistry.cs](src/HgvMate.Mcp/Repos/SqliteRepoRegistry.cs) — schema and `UpdateLastSyncedAsync`

**Impact:** Cannot tell if a repo has never been synced vs. sync was attempted and failed. The `/health` endpoint shows "never" for both cases.

**Fix:**
- Add columns: `last_error TEXT`, `last_error_at TEXT`, `failed_sync_count INTEGER DEFAULT 0`, `sync_state TEXT DEFAULT 'pending'`
- `sync_state` enum: `pending` → `syncing` → `synced` | `failed`
- Update `RepoSyncService` to write error state on catch, clear it on success
- Surface error details in `/health` and `/api/repositories/{name}/status`

---

## 4. HTTP 500 on Unhandled Exceptions in API

**Problem:** When `registry.AddAsync` or other database operations throw (e.g., SQLite busy/locked during concurrent writes), the exception propagates as an unstructured HTTP 500.

**Affected code:**
- [ApiEndpoints.cs](src/HgvMate.Mcp/Api/ApiEndpoints.cs) — all endpoints
- [Program.cs](src/HgvMate.Mcp/Program.cs) — no global exception middleware

**Impact:** Clients get `500` with no useful error body. The 4 failed repos during onboarding returned 500 with no detail.

**Fix:**
- Add global exception middleware that returns structured JSON: `{ "error": "...", "detail": "..." }`
- For database concurrency issues, return `503 Service Unavailable` with `Retry-After` header
- Log all unhandled exceptions with correlation IDs

---

## 5. Indexing Service — No Partial Failure Tracking

**Problem:** `IndexingService.IndexRepoAsync` continues past individual file failures (good) but doesn't report which files failed or how many.

**Affected code:**
- [IndexingService.cs](src/HgvMate.Mcp/Search/IndexingService.cs) — `IndexRepoAsync`

**Impact:** A repo shows as "indexed" with N chunks, but there's no indication that M files were skipped due to errors.

**Fix:**
- Return an `IndexResult` record: `{ FilesIndexed, ChunksCreated, FilesSkipped, Errors[] }`
- Persist indexing stats per repo (total files, indexed files, skipped files, last index duration)
- Surface in `/health` and status endpoints

---

## 6. Concurrent Sync Overload — No Throttling

**Problem:** `SyncAllAsync` and rapid sequential `AddRepository` calls each spawn `Task.Run` for sync. Adding 35+ repos simultaneously creates 35 concurrent git clones + 35 ONNX indexing passes, overwhelming memory and causing OOM (ExitCode 137).

**Affected code:**
- [RepoSyncService.cs](src/HgvMate.Mcp/Repos/RepoSyncService.cs) — `SyncAllAsync`
- [ApiEndpoints.cs](src/HgvMate.Mcp/Api/ApiEndpoints.cs) — POST `/api/repositories`

**Impact:** Container crashed with OOM at 2 GB RAM when adding 33 repos. Required upgrade to 8 GB.

**Fix:**
- Use a `Channel<RepoRecord>` or `SemaphoreSlim` to limit concurrent syncs (e.g., max 3 at a time)
- Queue incoming sync requests and process them in order
- Report queue position in status endpoint

---

## 7. Missing Structured Logging

**Problem:** Logging uses `ILogger` with string interpolation templates (good) but there's no structured context for filtering by repo name, operation type, or error category across log entries.

**Affected code:** All services

**Fix:**
- Use `ILogger.BeginScope` with repo name for all sync/index operations
- Add log categories: `Sync`, `Index`, `Search`, `Admin`
- Consider adding a log sink that writes to SQLite for in-app log viewing via `/api/logs`

---

## 8. Health Endpoint Doesn't Show Errors

**Problem:** `/health` shows repos with `LastSynced: "never"` but doesn't distinguish between "queued for sync" and "sync failed". No error messages are surfaced.

**Affected code:**
- [ApiEndpoints.cs](src/HgvMate.Mcp/Api/ApiEndpoints.cs) — `MapHealthEndpoint`

**Fix:**
- Add `syncState`, `lastError`, `failedSyncCount` to the repo status details
- Add an aggregate `failedRepos` count to the top-level health response
- Change top-level status from "healthy" to "degraded" when repos have sync failures

---

## Priority Order

| # | Issue | Effort | Impact |
|---|-------|--------|--------|
| 3 | Persistent error state in DB | Medium | High — unlocks all status improvements |
| 1 | Fire-and-forget error capture | Medium | High — root cause of silent failures |
| 6 | Concurrent sync throttling | Medium | High — prevents OOM crashes |
| 2 | Retry on transient git failures | Low | High — eliminates manual retry |
| 4 | Global exception middleware | Low | Medium — better API error responses |
| 8 | Health endpoint error display | Low | Medium — operational visibility |
| 5 | Indexing partial failure tracking | Low | Low — nice-to-have |
| 7 | Structured logging | Medium | Low — ops improvement |
