#!/bin/bash
set -e
set -o pipefail

command_exists() {
	command -v "$1" >/dev/null 2>&1
}

echo "Running HgvMate post-create setup..."

# Source .env for non-secret config (hostnames, usernames) if not already in environment
if [ -f /workspaces/HgvMate/.env ]; then
	echo "Loading environment from .env..."
	set -a
	# shellcheck disable=SC1091
	source /workspaces/HgvMate/.env
	set +a
fi

# Fix .dotnet directory ownership
echo "Fixing .dotnet directory ownership..."
sudo chown -R vscode:vscode /home/vscode/.dotnet || true

# Display .NET version and runtime details
echo "Checking .NET installation..."
dotnet --info
echo "Installed SDKs:"
dotnet --list-sdks || true
echo "Installed runtimes:"
dotnet --list-runtimes || true

# Ensure handy CLI tools are available (minimal set)
echo "Installing development CLI utilities..."
sudo apt-get update -y
sudo apt-get install -y jq ripgrep || echo "Warning: CLI utility installation failed, continuing..."

# Add vscode user to docker group
echo "Adding vscode user to docker group..."
if getent group docker >/dev/null 2>&1; then
	sudo usermod -aG docker vscode || true
else
	echo "Docker group not present; skipping usermod"
fi

# Set docker socket permissions
echo "Setting docker socket permissions..."
if [ -S /var/run/docker.sock ]; then
	sudo chmod 666 /var/run/docker.sock || true
else
	echo "Docker socket not present; skipping chmod"
fi

# Verify docker is working
echo "Verifying Docker installation..."
if command_exists docker; then
	docker --version
else
	echo "Warning: docker CLI not found on PATH"
fi

# Verify Node.js (needed for GitNexus)
echo "Verifying Node.js installation..."
if command_exists node; then
	echo "Node.js: $(node --version)"
	echo "npm: $(npm --version)"
else
	echo "Warning: node not found on PATH"
fi

# Setup SSH agent
echo "Setting up SSH agent..."
if [ -z "$SSH_AUTH_SOCK" ]; then
	echo "Starting new SSH agent..."
	eval "$(ssh-agent -s)"
else
	echo "Using existing SSH agent at $SSH_AUTH_SOCK"
fi

# Restore SSH key from Codespace secret (survives rebuilds)
if [ -n "${SSH_PRIVATE_KEY:-}" ] && [ ! -f /home/vscode/.ssh/id_ed25519 ]; then
	echo "Restoring SSH key from Codespace secret..."
	mkdir -p /home/vscode/.ssh
	echo "$SSH_PRIVATE_KEY" > /home/vscode/.ssh/id_ed25519
	chmod 600 /home/vscode/.ssh/id_ed25519
	ssh-keygen -y -f /home/vscode/.ssh/id_ed25519 > /home/vscode/.ssh/id_ed25519.pub
	chmod 644 /home/vscode/.ssh/id_ed25519.pub
	echo "SSH key restored."
fi

# Create Docker contexts for remote hosts (survives rebuilds)
echo "Setting up Docker contexts for remote hosts..."
create_docker_context() {
	local name="$1" user="$2" host="$3"
	if [ -z "$host" ]; then
		echo "  Skipping ${name}: host not set"
		return
	fi
	local endpoint="ssh://${user}@${host}"
	# Remove stale context if it exists, then recreate
	docker context rm "$name" --force >/dev/null 2>&1 || true
	docker context create "$name" --docker "host=${endpoint}" >/dev/null 2>&1
	echo "  Created context '${name}' -> ${endpoint}"
}

create_docker_context "mac" "${MAC_USER:-roys}" "${MAC_HOST:-}"
create_docker_context "lxc" "${LXC_USER:-root}" "${LXC_HOST:-}"
create_docker_context "proxmox" "${PVE_USER:-root}" "${PVE_HOST:-}"

# Add remote hosts to SSH known_hosts (avoids interactive prompt)
echo "Scanning SSH host keys for remote hosts..."
mkdir -p /home/vscode/.ssh
touch /home/vscode/.ssh/known_hosts
for host in ${MAC_HOST:-} ${LXC_HOST:-} ${PVE_HOST:-}; do
	if [ -n "$host" ] && ! grep -q "^${host}" /home/vscode/.ssh/known_hosts 2>/dev/null; then
		ssh-keyscan -H "$host" >> /home/vscode/.ssh/known_hosts 2>/dev/null && \
			echo "  Added host key for ${host}" || \
			echo "  Warning: could not scan host key for ${host} (host may be offline)"
	fi
done

# Try to load SSH keys if available
if compgen -G "/home/vscode/.ssh/id_*" >/dev/null 2>&1; then
	for key in /home/vscode/.ssh/id_*; do
		if [[ -f "$key" && "$key" != *.pub ]]; then
			if ssh-add "$key" >/dev/null 2>&1; then
				echo "Loaded SSH key: $key"
			else
				echo "Warning: Failed to load key $key (may require passphrase)"
			fi
		fi
	done
else
	echo "No default SSH keys found. You can add keys manually with ssh-add if needed."
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
