# Dev Container Template — RoySalisbury Standard

Generalized devcontainer setup derived from patterns across all RoySalisbury repos.
Use as a starting point for new repos or when migrating existing ones.

## Quick Start

Copy the three files below into `.devcontainer/` in your repo:
1. `Dockerfile`
2. `devcontainer.json`
3. `post-create.sh`

Then customize the `[REPO-SPECIFIC]` sections for your project.

---

## Dockerfile

```dockerfile
# Use base:ubuntu so the dotnet feature controls the exact SDK version.
# Avoids: (1) stale RC builds in dotnet:1-X.0, (2) Yarn APT source bug.
FROM mcr.microsoft.com/devcontainers/base:ubuntu

# Restore NuGet XML docs for richer IntelliSense.
ENV NUGET_XMLDOC_MODE=
```

That's it — no Yarn cleanup, no RC removal, no NVM cleanup.

---

## devcontainer.json

```jsonc
{
  "name": "[REPO NAME] Dev Container",
  "build": {
    "dockerfile": "Dockerfile"
  },

  // ─── Host Requirements ─────────────────────────────────────
  "hostRequirements": {
    "cpus": 2,       // Bump to 4 for heavy builds
    "memory": "4gb", // Bump to 8gb for multi-project workspaces
    "storage": "32gb"
  },

  // ─── Features ──────────────────────────────────────────────
  "features": {
    // Core: .NET SDK (always latest GA for the specified major)
    "ghcr.io/devcontainers/features/dotnet:2": {
      "version": "10.0"
      // Multi-targeting? Add: "additionalVersions": "9.0,8.0"
    },

    // Core: Docker-in-Docker (for container builds/tests)
    "ghcr.io/devcontainers/features/docker-in-docker:2": {},

    // Core: GitHub CLI
    "ghcr.io/devcontainers/features/github-cli:1": {},

    // Core: Zsh + Oh My Zsh (consistent terminal experience)
    "ghcr.io/devcontainers/features/common-utils:2": {
      "installZsh": true,
      "installOhMyZsh": true,
      "configureZshAsDefaultShell": true,
      "username": "vscode",
      "upgradePackages": false
    }

    // ─── Optional features (uncomment as needed) ──────────
    // "ghcr.io/devcontainers/features/node:1": { "version": "22" },
    // "ghcr.io/devcontainers/features/azure-cli:1": {},
    // "ghcr.io/tailscale/codespace/tailscale:1": {}
  },

  // ─── VS Code Customizations ────────────────────────────────
  "customizations": {
    "codespaces": {
      "permissions": {
        "packages": "write",
        "actions": "write"
      }
    },
    "vscode": {
      "extensions": [
        "ms-dotnettools.csdevkit",
        "ms-dotnettools.csharp",
        "ms-dotnettools.vscode-dotnet-runtime",
        "ms-azuretools.vscode-docker",
        "GitHub.copilot",
        "GitHub.copilot-chat",
        "github.vscode-github-actions"
        // ─── Optional extensions ──────────────────────────
        // "ms-dotnettools.blazorwasm-companion",
        // "ms-mssql.mssql"
      ],
      "settings": {
        "dotnet.defaultSolution": "[SOLUTION].sln",
        "dotnet.completion.showCompletionItemsFromUnimportedNamespaces": true,
        "csharp.semanticHighlighting.enabled": true,
        "editor.formatOnSave": true,
        "editor.formatOnType": true,
        "editor.codeActionsOnSave": {
          "source.fixAll": "explicit",
          "source.organizeImports": "explicit"
        },
        "terminal.integrated.defaultProfile.linux": "zsh",
        "terminal.integrated.profiles.linux": {
          "zsh": { "path": "/bin/zsh" },
          "bash": { "path": "/bin/bash" }
        }
      }
    }
  },

  // ─── Environment ───────────────────────────────────────────
  "containerEnv": {
    "ASPNETCORE_ENVIRONMENT": "Development",
    "DOTNET_ENVIRONMENT": "Development"
  },

  // ─── [REPO-SPECIFIC] Secrets ───────────────────────────────
  // Add repo-specific secrets here via remoteEnv.
  // In Codespaces: set as Codespace user secrets.
  // Locally: auto-fetched from .env via gist (see post-create.sh).
  "remoteEnv": {
    // "MY_SECRET": "${localEnv:MY_SECRET}"
  },

  // ─── Ports ─────────────────────────────────────────────────
  // "forwardPorts": [5000, 8080],
  // "portsAttributes": {
  //   "5000": { "label": "API", "onAutoForward": "notify" }
  // },

  // ─── Mounts ────────────────────────────────────────────────
  // Common mounts (uncomment as needed):
  // "initializeCommand": "mkdir -p \"$HOME/.microsoft/usersecrets\"",
  // "mounts": [
  //   "source=x509stores,target=/home/vscode/.dotnet/corefx/cryptography/x509stores,type=volume",
  //   "source=${localEnv:HOME}${localEnv:USERPROFILE}/.microsoft/usersecrets,target=/home/vscode/.microsoft/usersecrets,type=bind,consistency=cached"
  // ],

  // ─── Lifecycle ─────────────────────────────────────────────
  "postCreateCommand": "bash .devcontainer/post-create.sh",
  "remoteUser": "vscode"
}
```

---

## post-create.sh

```bash
#!/bin/bash
set -e
set -o pipefail

command_exists() { command -v "$1" >/dev/null 2>&1; }

echo "Running post-create setup..."

# ══════════════════════════════════════════════════════════════
# 1. SECRET BOOTSTRAP (Gist-based .env for local devcontainers)
# ══════════════════════════════════════════════════════════════

# [REPO-SPECIFIC] Set your private gist ID here (or leave empty to skip)
ENV_GIST=""  # e.g. "1f014918502877f0c37738fa733dad65"
WORKSPACE="/workspaces/$(basename "$PWD")"
ENV_FILE="$WORKSPACE/.env"

if [ -n "$ENV_GIST" ] && [ ! -f "$ENV_FILE" ] && [ -z "${CODESPACES:-}" ]; then
    echo "No .env file found. Attempting to fetch from GitHub Gist..."
    GIST_TOKEN="${GH_TOKEN:-${GITHUB_TOKEN:-}}"
    if [ -n "$GIST_TOKEN" ]; then
        if GH_TOKEN="$GIST_TOKEN" gh gist view "$ENV_GIST" --raw --filename .env \
                > "$ENV_FILE" 2>/dev/null; then
            chmod 600 "$ENV_FILE"
            echo "✅ Fetched .env from GitHub Gist."
        else
            echo "⚠  Could not fetch .env from Gist."
            rm -f "$ENV_FILE"
        fi
    else
        echo "⚠  No GitHub token available to fetch .env from Gist."
    fi
fi

if [ -f "$ENV_FILE" ]; then
    echo "Loading environment from .env..."
    set -a; source "$ENV_FILE"; set +a

    # Persist .env sourcing in .bashrc and .zshrc for future shells
    for RC_FILE in /home/vscode/.bashrc /home/vscode/.zshrc; do
        MARKER="# >>> env-auto >>>"
        if [ -f "$RC_FILE" ] && ! grep -qF "$MARKER" "$RC_FILE" 2>/dev/null; then
            cat >> "$RC_FILE" <<ENVBLOCK

$MARKER
if [ -f $ENV_FILE ]; then set -a; source $ENV_FILE; set +a; fi
# <<< env-auto <<<
ENVBLOCK
        fi
    done
fi

# ══════════════════════════════════════════════════════════════
# 2. .NET SETUP
# ══════════════════════════════════════════════════════════════

echo "Fixing .dotnet directory ownership..."
sudo chown -R vscode:vscode /home/vscode/.dotnet || true

echo "Checking .NET installation..."
dotnet --info
echo "Installed SDKs:"; dotnet --list-sdks || true
echo "Installed runtimes:"; dotnet --list-runtimes || true

# [REPO-SPECIFIC] Uncomment to install global tools:
# dotnet tool install --global dotnet-ef 2>/dev/null || true

# [REPO-SPECIFIC] Uncomment to restore NuGet packages:
# dotnet restore

# ══════════════════════════════════════════════════════════════
# 3. CLI TOOLS
# ══════════════════════════════════════════════════════════════

echo "Installing development CLI utilities..."
sudo apt-get update -y
sudo apt-get install -y --no-install-recommends jq ripgrep \
    || echo "Warning: CLI utility installation failed, continuing..."

# ══════════════════════════════════════════════════════════════
# 4. DOCKER
# ══════════════════════════════════════════════════════════════

echo "Configuring Docker..."
if getent group docker >/dev/null 2>&1; then
    sudo usermod -aG docker vscode || true
fi
if [ -S /var/run/docker.sock ]; then
    sudo chmod 666 /var/run/docker.sock || true
fi
if command_exists docker; then
    docker --version
fi

# [REPO-SPECIFIC] Docker contexts for remote deployment
# Uses env vars so hosts aren't hardcoded in the script.
create_docker_context() {
    local name="$1" user="$2" host="$3"
    if [ -z "$user" ] || [ -z "$host" ]; then return; fi
    if docker context inspect "$name" >/dev/null 2>&1; then
        echo "Docker context '$name' already exists."
    else
        echo "Creating docker context '$name' → ssh://$user@$host"
        docker context create "$name" \
            --description "Remote engine: $name" \
            --docker "host=ssh://$user@$host"
    fi
}
# Example (uncomment and set env vars in .env / Codespace secrets):
# create_docker_context "proxmox" "${PVE_USER:-}" "${PVE_HOST:-}"
# create_docker_context "mac"     "${MAC_USER:-}" "${MAC_HOST:-}"

# ══════════════════════════════════════════════════════════════
# 5. SSH
# ══════════════════════════════════════════════════════════════

echo "Setting up SSH..."
if [ -z "$SSH_AUTH_SOCK" ]; then
    eval "$(ssh-agent -s)"
else
    echo "Using existing SSH agent at $SSH_AUTH_SOCK"
fi

# Restore SSH key from env var (single-line with \n escapes)
if [ -n "${SSH_PRIVATE_KEY:-}" ] && [ ! -f /home/vscode/.ssh/id_ed25519 ]; then
    mkdir -p /home/vscode/.ssh
    printf '%b\n' "$SSH_PRIVATE_KEY" > /home/vscode/.ssh/id_ed25519
    chmod 600 /home/vscode/.ssh/id_ed25519
    if ssh-keygen -y -f /home/vscode/.ssh/id_ed25519 > /home/vscode/.ssh/id_ed25519.pub 2>/dev/null; then
        chmod 644 /home/vscode/.ssh/id_ed25519.pub
        echo "SSH key restored and validated."
    else
        echo "⚠  SSH_PRIVATE_KEY is invalid. Removing."
        rm -f /home/vscode/.ssh/id_ed25519 /home/vscode/.ssh/id_ed25519.pub
    fi
fi

# Load SSH keys
if compgen -G "/home/vscode/.ssh/id_*" >/dev/null 2>&1; then
    for key in /home/vscode/.ssh/id_*; do
        [[ -f "$key" && "$key" != *.pub ]] && ssh-add "$key" 2>/dev/null && echo "Loaded: $key" || true
    done
fi

# [REPO-SPECIFIC] Scan known hosts for remote servers:
# for host in "${PVE_HOST:-}" "${MAC_HOST:-}"; do
#     [ -n "$host" ] && ssh-keyscan -H "$host" >> /home/vscode/.ssh/known_hosts 2>/dev/null
# done

# ══════════════════════════════════════════════════════════════
# 6. [REPO-SPECIFIC] ADDITIONAL SETUP
# ══════════════════════════════════════════════════════════════

# Uncomment as needed:
# dotnet dev-certs https --clean && dotnet dev-certs https  # HTTPS cert
# dotnet restore MyProject.sln                               # NuGet restore
# npm install                                                 # Node packages

echo "Post-create setup completed!"
```

---

## export-env.sh (Optional — for gist-based secrets)

Run inside a Codespace to export secrets to the private gist:

```bash
#!/bin/bash
set -euo pipefail

ENV_FILE="/workspaces/$(basename "$PWD")/.env"
ENV_GIST=""  # [REPO-SPECIFIC] Your private gist ID

if [ -z "${CODESPACES:-}" ]; then
    echo "⚠  Not a Codespace. Secrets may not be available."
    read -r -p "Continue? [y/N] " c; [[ "$c" =~ ^[Yy]$ ]] || exit 1
fi

# [REPO-SPECIFIC] List the env vars to export:
cat > "$ENV_FILE" <<EOF
# Auto-generated from Codespace secrets — $(date -u +%Y-%m-%dT%H:%M:%SZ)
# MY_SECRET=${MY_SECRET:-}
EOF

# SSH key: escape newlines for single-line storage
if [ -f /home/vscode/.ssh/id_ed25519 ]; then
    KEY_ESCAPED=$(awk '{printf "%s\\n", $0}' /home/vscode/.ssh/id_ed25519 | sed 's/\\n$//')
    echo "SSH_PRIVATE_KEY=$KEY_ESCAPED" >> "$ENV_FILE"
fi

chmod 600 "$ENV_FILE"
echo "✅ Generated $ENV_FILE"

# Update gist if ID is set
if [ -n "$ENV_GIST" ]; then
    GH_TOKEN="$GITHUB_TOKEN" gh gist edit "$ENV_GIST" "$ENV_FILE"
    echo "✅ Gist updated."
fi
```

---

## Migration Checklist

When migrating an existing repo to this template:

- [ ] Replace Dockerfile contents with the 2-line version above
- [ ] Update `devcontainer.json` features to use `dotnet:2` instead of base image .NET
- [ ] Remove Yarn APT source workarounds from Dockerfile
- [ ] Remove RC/preview SDK cleanup from Dockerfile
- [ ] Add `common-utils:2` feature if missing (zsh)
- [ ] Add standard VS Code settings (formatOnSave, etc.)
- [ ] Add missing extensions (`vscode-github-actions`, `vscode-dotnet-runtime`)
- [ ] Merge post-create.sh with template (keep repo-specific sections)
- [ ] Create private gist for .env if repo has secrets
- [ ] Replace hardcoded IPs in docker contexts with env vars
- [ ] Add `.env` to `.gitignore`
- [ ] Test in both Codespaces and local devcontainer
