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
# libgomp1 is required by ONNX Runtime for OpenMP-based CPU parallelism
RUN apt-get update && apt-get install -y --no-install-recommends \
    git curl ca-certificates libstdc++6 libicu74 libgomp1 \
    && rm -rf /var/lib/apt/lists/*

# Node.js 22 (required by GitNexus / @ladybugdb/core which needs Node 20+ and glibc 2.38+)
# Build tools are needed for tree-sitter native modules but purging them cascades to nodejs,
# so we simply leave them installed (adds ~100 MB to image, acceptable for correctness).
RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && \
    apt-get install -y --no-install-recommends nodejs make g++ python3 && \
    npm install -g gitnexus@latest && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

ENV HF_ONNX_BASE="https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/"

# Download architecture-specific quantized ONNX models (~23 MB each, faster CPU inference)
# and FP32 model as fallback (~90 MB). OnnxEmbedder picks the best match at runtime.
ARG TARGETARCH
RUN mkdir -p /app/models && \
    if [ "$TARGETARCH" = "arm64" ]; then \
      curl -fSL -o /app/models/model_qint8_arm64.onnx \
        "${HF_ONNX_BASE}model_qint8_arm64.onnx" ; \
    else \
      curl -fSL -o /app/models/model_quint8_avx2.onnx \
        "${HF_ONNX_BASE}model_quint8_avx2.onnx" && \
      curl -fSL -o /app/models/model_qint8_avx512_vnni.onnx \
        "${HF_ONNX_BASE}model_qint8_avx512_vnni.onnx" ; \
    fi && \
    curl -fSL -o /app/models/model.onnx \
      "${HF_ONNX_BASE}model.onnx"

VOLUME /data
ENV HgvMate__DataPath=/data
# Clone repos to ephemeral local storage (fast SSD, not network-mounted volume)
ENV RepoSync__ClonePath=/tmp/hgvmate/repos
# Reserve 1 GB free space on ephemeral disk to prevent filling it
ENV RepoSync__MinFreeDiskSpaceMb=1024
ENV ASPNETCORE_URLS=http://+:5000
# Repos cloned by previous containers may have different UIDs on the /data volume
RUN git config --global --add safe.directory '*'
ENTRYPOINT ["/app/HgvMate.Mcp"]

