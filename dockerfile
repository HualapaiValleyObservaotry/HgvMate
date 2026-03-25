# ── Stage 1: build ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/HgvMate.Mcp/HgvMate.Mcp.csproj \
    -c Release \
    -r linux-musl-x64 \
    --self-contained true \
    -p:PublishSingleFile=false \
    -o /app

# ── Stage 2: runtime ──────────────────────────────────────────────────────────
FROM alpine:3.21 AS runtime

# git (repo operations) + Node.js / npm (GitNexus) + curl (model download)
# .NET self-contained native deps on musl: libstdc++ icu-libs
RUN apk add --no-cache git nodejs npm curl libstdc++ icu-libs && \
    npm install -g gitnexus@latest

WORKDIR /app
COPY --from=build /app .

# Download the all-MiniLM-L6-v2 ONNX model (~80 MB)
RUN mkdir -p /app/models && \
    curl -fSL -o /app/models/all-MiniLM-L6-v2.onnx \
    "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx"

VOLUME /data
ENV HgvMate__DataPath=/data
ENTRYPOINT ["/app/HgvMate.Mcp"]

