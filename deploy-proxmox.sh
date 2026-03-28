#!/usr/bin/env bash
# deploy-proxmox.sh — Deploy HgvMate to a Proxmox Docker host via SSH
#
# Usage:
#   ./deploy-proxmox.sh [proxmox-host] [image-tag]
#
# Examples:
#   ./deploy-proxmox.sh 192.168.2.104              # latest image
#   ./deploy-proxmox.sh 192.168.2.104 c73c08e      # specific SHA tag
#
# Prerequisites on the Proxmox host:
#   - Docker Engine installed (apt install docker.io docker-compose-plugin)
#   - SSH access (key-based recommended)
#   - mkdir -p /opt/hgvmate/data
#   - Copy docker-compose.proxmox.yml to /opt/hgvmate/
#   - Create /opt/hgvmate/.env with GITHUB_TOKEN and AZURE_DEVOPS_PAT

set -euo pipefail

HOST="${1:-${LXC_HOST:-192.168.2.104}}"
USER="${LXC_USER:-root}"
TAG="${2:-latest}"
IMAGE="ghcr.io/roysalisbury/hgvmate:${TAG}"
REMOTE_DIR="/opt/hgvmate"

echo "==> Deploying ${IMAGE} to ${USER}@${HOST}..."

# 1. Copy compose file if it has changed
echo "==> Syncing compose file..."
scp -q docker-compose.proxmox.yml "${USER}@${HOST}:${REMOTE_DIR}/docker-compose.yml"

# 2. Pull the latest image and restart
echo "==> Pulling image and restarting..."
ssh "${USER}@${HOST}" bash -s <<EOF
  set -euo pipefail
  cd ${REMOTE_DIR}
  docker compose pull hgvmate
  docker compose up -d --force-recreate hgvmate
  echo "==> Deployed. Waiting for health..."
  sleep 3
  docker compose ps
  echo ""
  echo "MCP endpoint: http://${HOST}:5000/mcp"
  echo "Dashboard:    http://${HOST}:18888"
EOF

echo "==> Done."
