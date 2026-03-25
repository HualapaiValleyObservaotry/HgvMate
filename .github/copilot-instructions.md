# Project Guidelines

## Architecture

HgvMate is an MCP server for AI-assisted code intelligence. See [docs/development-plan.md](../docs/development-plan.md) for architecture decisions and implementation phases.

- **Runtime:** .NET 10, ONNX Runtime, GitNexus (Node.js), SQLite
- **Layout:** `src/HgvMate.Mcp/` (main), `tests/HgvMate.Tests/` (tests)
- **Domain folders:** `Configuration/`, `Data/`, `Repos/`, `Search/`, `Tools/`, `Api/`

## Build and Test

```bash
dotnet build
dotnet test
```

Docker build and run:
```bash
docker build -t hgvmate .
docker run -p 5000:5000 -e AZURE_DEVOPS_PAT=<pat> hgvmate
```

## Testing Conventions

- **Framework:** MSTest with `[TestClass]` / `[TestMethod]`
- **Parallel execution:** Enabled at method level (see `MSTestSettings.cs`)
- **Test categories:** Use `[TestCategory("...")]` to classify tests:
  - `Unit` — standalone tests with mocked dependencies, no Docker, no network
  - `Docker` — tests that validate behavior inside the Docker container
  - `Integration` — end-to-end tests against a running instance
  - `LiveOnnx` — tests requiring a real ONNX model
- **Mocking:** Use constructor injection and `virtual` methods for testability (see `RepoSyncServiceTests.cs`)
- **Assertion order:** MSTest uses `Assert.AreEqual(expected, actual)` — expected value first

## Code Style

- Use `ILogger<T>` for all logging, never `Console.Write`
- Use `async/await` throughout — no `.Result` or `.Wait()` blocking
- Prefer records for DTOs and immutable data
- Keep MCP tool methods thin — delegate to service classes

## Copilot Coding Agent Workflow

When working on issues assigned to `copilot-swe-agent`, follow this workflow:

1. **Create a feature branch** from `main` — do not work directly on `main`
2. **Make changes** with accompanying tests at the appropriate level (Unit, Docker, Integration)
3. **Run `dotnet test`** and fix any failures before opening a PR
4. **Open a regular PR** (not a draft PR) — **NEVER open a draft PR**. Draft PRs do not trigger the required automated review workflow and will block the process
5. **Wait for the automated code review** to complete — this is a **blocking requirement**. Do not merge, do not close, do not mark as done until the automated review has finished and posted its comments
6. **Address all review comments** — read every comment from the automated review, make the requested changes, and push fix commits to the same branch. Do not dismiss or ignore review feedback
7. **After addressing comments, wait again** — the review may run another pass after your fixes. Repeat steps 5–6 until the review is clean
8. **Do not force-push** or rewrite history on the PR branch
9. **Ensure CI passes** (GitHub Actions: Docker build + publish) before merge
10. **Only merge after**: all review comments are resolved AND CI is green

### Environment Setup for CI

The Copilot coding agent CI environment has a firewall that blocks outbound connections by default. If tests or builds need to download external resources, those URLs must be allowlisted in the repository's [Copilot coding agent settings](https://github.com/RoySalisbury/HgvMate/settings/copilot/coding_agent). Currently allowlisted:
- `huggingface.co` — ONNX model downloads
- `registry.npmjs.org` — npm packages for GitNexus
