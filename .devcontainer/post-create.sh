#!/bin/bash
set -e
set -o pipefail

echo "Running HgvMate post-create setup..."

# ─────────────────────────────────────────────────────────────────────
# Load shared devcontainer base script (common to all RoySalisbury repos)
# Provides: dc_bootstrap_env, dc_setup_dotnet, dc_install_cli,
#           dc_setup_docker, dc_setup_ssh, dc_create_contexts, dc_scan_hosts
# ─────────────────────────────────────────────────────────────────────
BASE_GIST="bceb71a9120e4d393b68308a03399ca5"

_load_base_script() {
	# Resolve a GitHub token: env vars → gh auth → VS Code git credential helper
	local token="${GH_TOKEN:-${GITHUB_TOKEN:-}}"
	if [ -z "$token" ] && command -v gh >/dev/null 2>&1; then
		token=$(gh auth token 2>/dev/null) || true
	fi
	if [ -z "$token" ] && command -v git >/dev/null 2>&1; then
		token=$(printf 'protocol=https\nhost=github.com\n' | GIT_TERMINAL_PROMPT=0 git credential fill 2>/dev/null | grep '^password=' | head -1 | cut -d= -f2-) || true
	fi

	# Try gh first (works with private gists + authenticated), fall back to curl
	if command -v gh >/dev/null 2>&1 && [ -n "$token" ]; then
		GH_TOKEN="$token" gh gist view "$BASE_GIST" --raw --filename devcontainer-base.sh 2>/dev/null && return 0
	fi
	# Public gist — curl works without auth (versionless raw URL)
	curl -fsSL "https://gist.githubusercontent.com/RoySalisbury/${BASE_GIST}/raw/devcontainer-base.sh" 2>/dev/null && return 0
	return 1
}

BASE_SCRIPT=$(_load_base_script || true)
if [ -n "$BASE_SCRIPT" ]; then
	eval "$BASE_SCRIPT"
else
	echo "⚠  Could not load devcontainer-base.sh from gist. Continuing without shared setup."
fi

# ─────────────────────────────────────────────────────────────────────
# Resolve and export a GitHub token so base script functions can use it
# (In local devcontainers, GH_TOKEN/GITHUB_TOKEN aren't set by default)
# ─────────────────────────────────────────────────────────────────────
if [ -z "${GH_TOKEN:-}" ] && [ -z "${GITHUB_TOKEN:-}" ]; then
	_resolved_token=""
	if command -v gh >/dev/null 2>&1; then
		_resolved_token=$(gh auth token 2>/dev/null) || true
	fi
	if [ -z "$_resolved_token" ] && command -v git >/dev/null 2>&1; then
		_resolved_token=$(printf 'protocol=https\nhost=github.com\n' | GIT_TERMINAL_PROMPT=0 git credential fill 2>/dev/null | grep '^password=' | head -1 | cut -d= -f2-) || true
	fi
	if [ -n "$_resolved_token" ]; then
		export GH_TOKEN="$_resolved_token"
		echo "Resolved GitHub token from credential helper (${#_resolved_token} chars)"
	fi
	unset _resolved_token
fi

# ─────────────────────────────────────────────────────────────────────
# Shared setup (env bootstrap, .NET, CLI tools, Docker, SSH, contexts)
# ─────────────────────────────────────────────────────────────────────
HGVMATE_ENV_GIST="1f014918502877f0c37738fa733dad65"

if type dc_bootstrap_env >/dev/null 2>&1; then
	dc_bootstrap_env "$HGVMATE_ENV_GIST" "/workspaces/HgvMate"
	dc_setup_dotnet
	dc_install_cli
	dc_setup_docker
	dc_setup_ssh
	dc_create_contexts
	dc_scan_hosts ${MAC_HOST:-} ${LXC_HOST:-} ${PVE_HOST:-}
else
	echo "⚠  Base script not loaded — running inline fallback..."
	# Minimal fallback if gist is unreachable
	sudo chown -R vscode:vscode /home/vscode/.dotnet || true
	dotnet --info
	sudo apt-get update -y && sudo apt-get install -y jq ripgrep || true
fi

# ─────────────────────────────────────────────────────────────────────
# HgvMate-specific setup
# ─────────────────────────────────────────────────────────────────────

# Verify Node.js (needed for GitNexus)
echo "Verifying Node.js installation..."
if command -v node >/dev/null 2>&1; then
	echo "Node.js: $(node --version)"
	echo "npm: $(npm --version)"
else
	echo "Warning: node not found on PATH"
fi

# Ensure .data directory exists for SQLite DBs and cloned repos
echo "Ensuring data directory..."
mkdir -p /workspaces/HgvMate/.data || true

# Restore .NET packages
echo "Restoring .NET packages..."
dotnet restore || echo "Warning: dotnet restore failed, continuing..."

# Build solution
echo "Building solution..."
dotnet build --no-restore || echo "Warning: dotnet build failed, continuing..."

echo
echo "Post-create setup completed successfully!"
