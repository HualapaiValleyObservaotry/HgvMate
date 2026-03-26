# ── Stage 1: build ────────────────────────────────────────────────────────────
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN dotnet_rid="linux-$([ "$TARGETARCH" = "amd64" ] && echo x64 || echo $TARGETARCH)" && \
    dotnet publish src/HgvMate.Mcp/HgvMate.Mcp.csproj \
    -c Release \
    -r "$dotnet_rid" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -o /app

# ── Stage 2: runtime ──────────────────────────────────────────────────────────
FROM ubuntu:24.04 AS runtime

# git (repo operations) + curl (model download) + .NET self-contained native deps
RUN apt-get update && apt-get install -y --no-install-recommends \
    git curl ca-certificates libstdc++6 libicu74 \
    && rm -rf /var/lib/apt/lists/*

# Node.js 22 (required by GitNexus / @ladybugdb/core which needs Node 20+ and glibc 2.38+)
RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && \
    apt-get install -y --no-install-recommends nodejs && \
    rm -rf /var/lib/apt/lists/* && \
    npm install -g gitnexus@latest

WORKDIR /app
COPY --from=build /app .

# Download the all-MiniLM-L6-v2 ONNX model (~80 MB)
RUN mkdir -p /app/models && \
    curl -fSL -o /app/models/all-MiniLM-L6-v2.onnx \
    "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx"

VOLUME /data
ENV HgvMate__DataPath=/data
# Clone repos to ephemeral local storage (fast SSD, not Azure Files SMB)
ENV RepoSync__ClonePath=/tmp/hgvmate/repos
# Reserve 1 GB free space on ephemeral disk to prevent filling it
ENV RepoSync__MinFreeDiskSpaceMb=1024
ENV ASPNETCORE_URLS=http://+:5000
ENTRYPOINT ["/app/HgvMate.Mcp"]

