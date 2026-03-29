# ── Stage 1: build ────────────────────────────────────────────────────────────
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
# amd64 → OpenVINO (Managed NuGet + external native libs from GitHub Release)
# arm64 → standard ORT NuGet (native libs bundled in package)
RUN dotnet_rid="linux-$([ "$TARGETARCH" = "amd64" ] && echo x64 || echo $TARGETARCH)" && \
  onnx_provider=$([ "$TARGETARCH" = "amd64" ] && echo openvino || echo cpu) && \
  dotnet publish src/HgvMate.Mcp/HgvMate.Mcp.csproj \
  -c Release \
  -r "$dotnet_rid" \
  --self-contained true \
  -p:PublishSingleFile=false \
  -p:OnnxProvider="$onnx_provider" \
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
  npm install -g gitnexus@1.4.10 && \
  rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

# ── OpenVINO native libraries (x86_64 only) ──────────────────────────────────
# Pre-extracted .so files from onnxruntime-openvino Python wheel, stored as a
# GitHub Release artifact. Provides Intel-optimized CPU inference via OpenVINO EP.
# ARM64 uses the standard ORT native libs bundled in the NuGet package.
# To update: run tools/extract-openvino-libs.sh, upload tarball to GitHub Release.
ARG TARGETARCH
ARG OPENVINO_LIBS_TAG=openvino-libs/v1.24.1
ARG GITHUB_REPO=RoySalisbury/HgvMate
RUN if [ "$TARGETARCH" = "amd64" ]; then \
  echo "Downloading OpenVINO native libs from GitHub Release ${OPENVINO_LIBS_TAG}..." && \
  curl -fSL "https://github.com/${GITHUB_REPO}/releases/download/${OPENVINO_LIBS_TAG}/linux-x64.tar.gz" \
  -o /tmp/openvino-libs.tar.gz && \
  tar -xzf /tmp/openvino-libs.tar.gz -C /app/ --strip-components=1 linux-x64/ && \
  rm /tmp/openvino-libs.tar.gz && \
  # .NET P/Invoke looks for 'onnxruntime.dll' — create symlink to the real .so
  cd /app && ln -sf libonnxruntime.so.1.24.1 onnxruntime.dll && \
  echo "OpenVINO libs installed:" && ls -lh /app/libonnxruntime*.so* /app/libopenvino*.so* /app/onnxruntime.dll 2>/dev/null ; \
  else \
  echo "TARGETARCH=${TARGETARCH}, skipping OpenVINO libs (not supported on this arch)." ; \
  fi

# Ensure transitive .so dependencies (libopenvino.so, libtbb.so, etc.) are found at runtime.
# Only needed when OpenVINO libs are present; harmless otherwise.
ENV LD_LIBRARY_PATH=/app

ENV HF_ONNX_BASE="https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/"

# Download architecture-specific quantized ONNX models (~23 MB each, faster CPU inference)
# and FP32 model as fallback (~90 MB). OnnxEmbedder picks the best match at runtime.
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
RUN git config --global --add safe.directory /data && \
  git config --global --add safe.directory /tmp/hgvmate/repos
ENTRYPOINT ["/app/HgvMate.Mcp"]

