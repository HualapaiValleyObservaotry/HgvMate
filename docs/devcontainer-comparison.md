# Dev Container Cross-Repo Comparison

Comparison of `.devcontainer/` setups across all RoySalisbury repositories.

## Repos Analyzed

| Repo | Base Image | .NET | Post-Create |
|------|-----------|------|-------------|
| **HgvMate** | `base:ubuntu` + dotnet feature | 10.0 | Full (gist, SSH, docker contexts) |
| HVO.Enterprise.Telemetry | `dotnet:1-10.0` | 10.0 + 8.0 | Standard + docker contexts + HTTPS cert |
| HVO.AiCodeReview | `dotnet:1-10.0` | 10.0 | Standard + docker contexts + HTTPS cert |
| HVO.SDK | `dotnet:1-10.0` | 10.0 | Minimal (restore only) |
| HVO.Workspace | `dotnet:1-10.0` | 10.0 + 9.0 + 8.0 | Full (clone repos, infra, gh auth) |
| HVO.WebSite | `dotnet:1-9.0` | (base image) | Standard + dotnet-ef + fonts |
| HVO.RoofController | `dotnet:1-9.0` | (base image) | Standard + GPIO/I2C + docker contexts |
| DevOpsMcp | `universal:2` | Aspire 9.0 | None |

## Key Findings

### 1. Base Image — Yarn & RC Problems

**6 of 7 repos** use `mcr.microsoft.com/devcontainers/dotnet:1-X.0` and all need a Dockerfile workaround to remove the broken Yarn APT source:

```dockerfile
RUN rm -f /etc/apt/sources.list.d/yarn.list || true
```

Enterprise.Telemetry also has an RC/preview SDK cleanup step. **HgvMate is the only repo** that solved both problems cleanly by switching to `base:ubuntu` + the devcontainer dotnet feature.

**Recommendation:** Migrate all repos to `base:ubuntu` + `dotnet:2` feature. Eliminates both the Yarn source and RC staleness issues. The Dockerfile becomes two lines instead of 10+.

### 2. Features

| Feature | HgvMate | Telemetry | AiCR | SDK | Workspace | WebSite | RoofCtrl |
|---------|:-------:|:---------:|:----:|:---:|:---------:|:-------:|:--------:|
| dotnet:2 | ✅ | ✅ | ✅ | ✅ | ✅ | — | — |
| docker-in-docker:2 | ✅ | ✅ | ✅ | — | ✅ | ✅ | ✅ |
| github-cli:1 | ✅ | — | — | ✅ | ✅ | ✅ | ✅ |
| common-utils:2 (zsh) | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| git:1 | — | ✅ | ✅ | ✅ | — | ✅ | ✅ |
| node:1 | ✅ | — | — | — | — | — | — |
| azure-cli:1 | ✅ | — | — | — | — | — | — |
| tailscale:1 | ✅ | — | — | — | — | — | — |

**Common core:** `dotnet:2`, `docker-in-docker:2`, `github-cli:1`, `common-utils:2`

**Gaps in HgvMate:** Missing `common-utils:2` (zsh/oh-my-zsh) — every other repo has it.

### 3. VS Code Settings

Most repos share identical settings. HgvMate only has `dotnet.defaultSolution`.

**Shared settings across 6 repos:**
```json
{
  "editor.formatOnSave": true,
  "editor.formatOnType": true,
  "editor.codeActionsOnSave": {
    "source.fixAll": "explicit",
    "source.organizeImports": "explicit"
  },
  "csharp.semanticHighlighting.enabled": true,
  "terminal.integrated.defaultProfile.linux": "zsh",
  "terminal.integrated.profiles.linux": {
    "zsh": { "path": "/bin/zsh" },
    "bash": { "path": "/bin/bash" }
  }
}
```

### 4. VS Code Extensions

**Universal (all repos):** `csdevkit`, `csharp`, `copilot`, `copilot-chat`

**Common (5+ repos):** `vscode-docker`, `vscode-github-actions`, `vscode-dotnet-runtime`

**Gaps in HgvMate:** Missing `vscode-github-actions` and `vscode-dotnet-runtime`.

### 5. Post-Create Script Patterns

**Shared core (all repos except SDK and DevOpsMcp):**
1. Fix .dotnet ownership: `sudo chown -R vscode:vscode /home/vscode/.dotnet`
2. Display .NET info: `dotnet --info`, `--list-sdks`, `--list-runtimes`
3. Install CLI tools: `apt-get install -y jq ripgrep`
4. Docker group + socket: `usermod -aG docker`, `chmod 666 /var/run/docker.sock`
5. SSH agent setup + key loading

**Optional layers (repo-specific):**
- **Docker contexts** — Telemetry, AiCR, RoofController, HgvMate (hardcoded IPs: `192.168.2.104`, `192.168.2.21`)
- **HTTPS dev cert** — Telemetry, AiCR, WebSite, RoofController
- **NuGet restore** — SDK, WebSite, RoofController
- **.NET global tools** — WebSite, Workspace (`dotnet-ef`)
- **Gist-based .env** — HgvMate only (most mature secrets approach)
- **GH_PAT auth** — Workspace only (for workflow PRs)

### 6. Secret Management

| Approach | Repos |
|----------|-------|
| Gist-based .env auto-fetch + Codespace secrets dual-path | HgvMate |
| `remoteEnv` with `${localEnv:}` | Workspace, AiCR |
| `containerEnv` with `${localEnv:}` | WebSite, Telemetry |
| None | SDK, RoofController, DevOpsMcp |

HgvMate's approach is the most robust — works identically in Codespaces and local devcontainers without manual secret setup.

### 7. Mounts

| Mount | Repos |
|-------|-------|
| x509stores volume | Telemetry, AiCR, Workspace, WebSite, RoofController |
| usersecrets bind | Telemetry, AiCR, Workspace, WebSite |
| Data volume | HgvMate |

### 8. Duplication

- **Enterprise.Telemetry ↔ AiCodeReview**: post-create.sh is 95% identical
- **WebSite ↔ RoofController**: post-create.sh is ~80% identical
- All four share the same docker context definitions (proxmox-home, rpi-home)

---

## Issues to Address Across Repos

1. **Migrate to `base:ubuntu`** — eliminates Yarn APT bug and RC staleness in all repos
2. **Add `common-utils:2` to HgvMate** — consistent zsh experience
3. **Add missing VS Code settings to HgvMate** — formatOnSave, codeActionsOnSave, etc.
4. **Add missing extensions to HgvMate** — `vscode-github-actions`, `vscode-dotnet-runtime`
5. **Adopt gist-based .env** — port HgvMate's approach to other repos
6. **Externalize docker context hosts** — use env vars instead of hardcoded IPs
7. **Consider a shared base script** — extract common post-create steps into a gist or shared snippet
