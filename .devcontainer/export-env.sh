#!/bin/bash
# ─────────────────────────────────────────────────────────────────────
# Export environment variables from a running Codespace to a .env file.
#
# Usage (run inside a Codespace):
#   bash .devcontainer/export-env.sh
#
# This generates /workspaces/HgvMate/.env with all the secrets and config
# that are injected via Codespace secrets. Copy that file to your local
# machine so local devcontainers work without Codespace secrets.
#
# The .env file is gitignored — it never leaves your machine.
# ─────────────────────────────────────────────────────────────────────
set -euo pipefail

ENV_FILE="/workspaces/HgvMate/.env"

if [ -z "${CODESPACES:-}" ]; then
	echo "WARNING: This does not appear to be a GitHub Codespace."
	echo "Environment variables may not contain Codespace secrets."
	read -r -p "Continue anyway? [y/N] " confirm
	if [[ ! "$confirm" =~ ^[Yy]$ ]]; then
		echo "Aborted."
		exit 1
	fi
fi

# Collect the variables we care about
declare -A VARS=(
	[AZURE_DEVOPS_PAT]="${AZURE_DEVOPS_PAT:-}"
	[GH_PAT]="${GH_PAT:-}"
	[PVE_HOST]="${PVE_HOST:-}"
	[PVE_USER]="${PVE_USER:-}"
	[PVE_TOKEN]="${PVE_TOKEN:-}"
	[MAC_HOST]="${MAC_HOST:-}"
	[MAC_USER]="${MAC_USER:-}"
	[LXC_HOST]="${LXC_HOST:-}"
	[LXC_USER]="${LXC_USER:-}"
	[TAILSCALE_AUTHKEY]="${TAILSCALE_AUTHKEY:-}"
)

# SSH key needs special handling (multiline → escaped single line)
SSH_KEY_ESCAPED=""
if [ -n "${SSH_PRIVATE_KEY:-}" ]; then
	# Key comes from env (may already be escaped or multiline)
	SSH_KEY_ESCAPED=$(echo "$SSH_PRIVATE_KEY" | awk '{printf "%s\\n", $0}' | sed 's/\\n$//')
elif [ -f /home/vscode/.ssh/id_ed25519 ]; then
	# Key is on disk (Codespace may have generated it)
	SSH_KEY_ESCAPED=$(awk '{printf "%s\\n", $0}' /home/vscode/.ssh/id_ed25519 | sed 's/\\n$//')
fi

# Check for missing values
MISSING=0
for key in "${!VARS[@]}"; do
	if [ -z "${VARS[$key]}" ]; then
		echo "⚠  $key is not set in the environment"
		MISSING=$((MISSING + 1))
	fi
done
if [ -z "$SSH_KEY_ESCAPED" ]; then
	echo "⚠  SSH_PRIVATE_KEY is not set and no key found at ~/.ssh/id_ed25519"
	MISSING=$((MISSING + 1))
fi

if [ "$MISSING" -gt 0 ]; then
	echo ""
	echo "$MISSING variable(s) are not set. They will be written as empty."
	read -r -p "Continue? [Y/n] " confirm
	if [[ "$confirm" =~ ^[Nn]$ ]]; then
		echo "Aborted."
		exit 1
	fi
fi

# Back up existing .env
if [ -f "$ENV_FILE" ]; then
	cp "$ENV_FILE" "${ENV_FILE}.bak"
	echo "Backed up existing .env to .env.bak"
fi

# Write .env
cat > "$ENV_FILE" <<EOF
# ─────────────────────────────────────────────────────────────────────
# HgvMate — Environment Variables
# ─────────────────────────────────────────────────────────────────────
# Generated from Codespace on $(date -u +"%Y-%m-%d %H:%M:%S UTC")
# .env is gitignored — never commit secrets.
# ─────────────────────────────────────────────────────────────────────

# Azure DevOps PAT — used for cloning repos and accessing work items
AZURE_DEVOPS_PAT=${VARS[AZURE_DEVOPS_PAT]}

# GitHub PAT — used for cloning GitHub repos
GH_PAT=${VARS[GH_PAT]}

# GitHub token — provided by VS Code / Codespaces automatically
GITHUB_TOKEN=

# Data directory — where SQLite DBs, cloned repos, and ONNX model are stored
HGVMATE_DATA_PATH=

# Proxmox API — used for deployment to local Proxmox host
PVE_HOST=${VARS[PVE_HOST]}
PVE_USER=${VARS[PVE_USER]}
PVE_TOKEN=${VARS[PVE_TOKEN]}

# Remote Docker hosts — used to create Docker contexts on dev container startup
MAC_HOST=${VARS[MAC_HOST]}
MAC_USER=${VARS[MAC_USER]}

# Proxmox LXC container running HgvMate (Docker context: lxc)
LXC_HOST=${VARS[LXC_HOST]}
LXC_USER=${VARS[LXC_USER]}

# Tailscale auth key — used by the Tailscale devcontainer feature
TAILSCALE_AUTHKEY=${VARS[TAILSCALE_AUTHKEY]}

# SSH private key — used to connect to remote Docker hosts (PVE, LXC, Mac)
# Stored as escaped single line; post-create.sh converts to file.
SSH_PRIVATE_KEY="${SSH_KEY_ESCAPED}"
EOF

chmod 600 "$ENV_FILE"

echo ""
echo "✅ Wrote $ENV_FILE"
echo ""
echo "Next steps:"
echo "  1. Copy this file to your local clone of the repo:"
echo "     scp <codespace>:/workspaces/HgvMate/.env /path/to/local/HgvMate/.env"
echo "  2. Or download it via VS Code: right-click .env → Download"
echo "  3. Rebuild your local devcontainer — all secrets will be available."
