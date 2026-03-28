#!/bin/bash
# post-attach.sh — Runs each time VS Code attaches to the container.
# Fetches the private .env gist when it's missing, and sets up a
# one-shot .zshrc hook as a fallback (the credential helper isn't
# always ready during postAttachCommand on first build).
set -e

HGVMATE_ENV_GIST="1f014918502877f0c37738fa733dad65"
BASE_GIST="bceb71a9120e4d393b68308a03399ca5"
ENV_FILE="/workspaces/HgvMate/.env"
WORKSPACE="/workspaces/HgvMate"

# ── Ensure .env sourcing + one-shot fetch hook in .zshrc (idempotent) ─
ZSHRC="$HOME/.zshrc"
SOURCE_LINE='[ -f /workspaces/HgvMate/.env ] && set -a && source /workspaces/HgvMate/.env && set +a'
FETCH_LINE='[ ! -f /workspaces/HgvMate/.env ] && [ -f /workspaces/HgvMate/.devcontainer/post-attach.sh ] && bash /workspaces/HgvMate/.devcontainer/post-attach.sh 2>/dev/null'
if ! grep -qF "$SOURCE_LINE" "$ZSHRC" 2>/dev/null; then
	{
		echo ""
		echo "# Auto-fetch and source HgvMate .env"
		echo "$FETCH_LINE"
		echo "$SOURCE_LINE"
	} >> "$ZSHRC"
	echo "Added .env fetch hook and sourcing to $ZSHRC"
fi

# ── Skip fetch if .env already exists ────────────────────────────────
if [ -f "$ENV_FILE" ]; then
	echo ".env already present — skipping fetch."
	exit 0
fi

echo "Fetching .env from private gist..."

# ── Resolve GitHub token ──────────────────────────────────────────────
token="${GH_TOKEN:-${GITHUB_TOKEN:-}}"
if [ -z "$token" ] && command -v gh >/dev/null 2>&1; then
	token=$(gh auth token 2>/dev/null) || true
fi
if [ -z "$token" ] && command -v git >/dev/null 2>&1; then
	token=$(printf 'protocol=https\nhost=github.com\n' | GIT_TERMINAL_PROMPT=0 git credential fill 2>/dev/null | grep '^password=' | head -1 | cut -d= -f2-) || true
fi

if [ -z "$token" ]; then
	echo "⚠  No GitHub token available yet — .env will be fetched when you open your first terminal."
	exit 0
fi

# ── Fetch private .env gist ──────────────────────────────────────────
env_content=""
if command -v gh >/dev/null 2>&1; then
	env_content=$(GH_TOKEN="$token" gh gist view "$HGVMATE_ENV_GIST" --raw --filename .env 2>/dev/null) || true
fi
if [ -z "$env_content" ]; then
	env_content=$(curl -fsSL -H "Authorization: token $token" \
		"https://api.github.com/gists/$HGVMATE_ENV_GIST" 2>/dev/null \
		| jq -r '.files[".env"].content // empty') || true
fi

if [ -z "$env_content" ]; then
	echo "⚠  Could not fetch .env from gist $HGVMATE_ENV_GIST"
	exit 0
fi

echo "$env_content" > "$ENV_FILE"
echo "✅ .env written to $ENV_FILE"

# ── Source .env and run env-dependent setup (SSH, Docker, contexts) ───
set -a
source "$ENV_FILE"
set +a

# Load base script for setup functions
BASE_SCRIPT=""
if command -v gh >/dev/null 2>&1; then
	BASE_SCRIPT=$(GH_TOKEN="$token" gh gist view "$BASE_GIST" --raw --filename devcontainer-base.sh 2>/dev/null) || true
fi
if [ -z "$BASE_SCRIPT" ]; then
	BASE_SCRIPT=$(curl -fsSL "https://gist.githubusercontent.com/RoySalisbury/${BASE_GIST}/raw/devcontainer-base.sh" 2>/dev/null) || true
fi

if [ -n "$BASE_SCRIPT" ]; then
	eval "$BASE_SCRIPT"
	export GH_TOKEN="$token"
	dc_setup_ssh
	dc_create_contexts
	dc_scan_hosts ${MAC_HOST:-} ${LXC_HOST:-} ${PVE_HOST:-}
	echo "✅ SSH, Docker contexts, and host scanning configured."
else
	echo "⚠  Could not load base script — SSH/Docker setup skipped."
fi
