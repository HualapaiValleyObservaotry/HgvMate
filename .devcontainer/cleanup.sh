#!/bin/bash
# Automated disk cleanup for the HgvMate dev container.
# Run manually with: bash .devcontainer/cleanup.sh
# Also runs automatically on every container start via postStartCommand.
set -euo pipefail

bytes_to_human() {
    local bytes=$1
    if (( bytes >= 1073741824 )); then
        printf "%d.%dG" $(( bytes / 1073741824 )) $(( (bytes % 1073741824) * 10 / 1073741824 ))
    elif (( bytes >= 1048576 )); then
        printf "%dM" $(( bytes / 1048576 ))
    elif (( bytes >= 1024 )); then
        printf "%dK" $(( bytes / 1024 ))
    else
        printf "%dB" "$bytes"
    fi
}

freed_total=0

track_freed() {
    local before=$1 after=$2 label=$3
    local diff=$(( before - after ))
    if (( diff > 0 )); then
        freed_total=$(( freed_total + diff ))
        echo "  Freed $(bytes_to_human $diff) from $label"
    fi
}

get_avail() {
    df --output=avail / | tail -1 | tr -d ' '
}

echo "=== HgvMate Dev Container Cleanup ==="
echo "Disk before: $(df -h / | awk 'NR==2{print $4}') available ($(df -h / | awk 'NR==2{print $5}') used)"
echo

avail_before=$(get_avail)

# --- npm / npx cache ---
echo "[1/6] npm caches..."
before=$(get_avail)
rm -rf ~/.npm/_npx/ ~/.npm/_logs/ 2>/dev/null || true
npm cache clean --force 2>/dev/null || true
after=$(get_avail)
track_freed "$before" "$avail_before" "npm caches"  # use avail_before baseline
track_freed "$before" "$after" "npm caches"

# --- NuGet temp caches ---
echo "[2/6] NuGet http-cache & temp..."
before=$(get_avail)
dotnet nuget locals http-cache --clear 2>/dev/null || true
dotnet nuget locals temp --clear 2>/dev/null || true
after=$(get_avail)
track_freed "$before" "$after" "NuGet temp caches"

# --- VS Code extension download cache ---
echo "[3/6] VS Code extension cache..."
before=$(get_avail)
rm -rf ~/.vscode-remote/extensionsCache/* 2>/dev/null || true
after=$(get_avail)
track_freed "$before" "$after" "VS Code extensionsCache"

# --- VS Code old logs (keep only the newest session) ---
echo "[4/6] VS Code old logs..."
before=$(get_avail)
if [ -d ~/.vscode-remote/data/logs ]; then
    find ~/.vscode-remote/data/logs/ -mindepth 1 -maxdepth 1 -type d \
        | sort | head -n -1 | xargs rm -rf 2>/dev/null || true
fi
after=$(get_avail)
track_freed "$before" "$after" "VS Code old logs"

# --- Temp files from integration tests ---
echo "[5/6] Temp test artifacts..."
before=$(get_avail)
rm -rf /tmp/HgvMateInteg_* /tmp/HgvMateTests_* 2>/dev/null || true
after=$(get_avail)
track_freed "$before" "$after" "temp test artifacts"

# --- Docker (only when no build is running) ---
echo "[6/6] Docker dangling resources..."
before=$(get_avail)
if pgrep -f "docker build" >/dev/null 2>&1; then
    echo "  Skipped — Docker build in progress"
else
    docker image prune -f 2>/dev/null || true
    docker volume prune -f 2>/dev/null || true
    docker builder prune -f --filter "unused-for=48h" 2>/dev/null || true
    after=$(get_avail)
    track_freed "$before" "$after" "Docker dangling resources"
fi

echo
avail_after=$(get_avail)
total_freed=$(( avail_after - avail_before ))
if (( total_freed > 0 )); then
    echo "Total freed: $(bytes_to_human $(( total_freed * 1024 )))"
else
    echo "Total freed: negligible (caches were already clean)"
fi
echo "Disk after:  $(df -h / | awk 'NR==2{print $4}') available ($(df -h / | awk 'NR==2{print $5}') used)"
