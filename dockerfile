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

# git (repo operations) + Node.js / npm (GitNexus)
RUN apk add --no-cache git nodejs npm \
    # .NET self-contained native deps on musl
    libstdc++ icu-libs

WORKDIR /app
COPY --from=build /app .

# Model placeholder directory — mount or COPY your ONNX model here at build time
RUN mkdir -p /app/models

VOLUME /data
ENV HgvMate__DataPath=/data
ENTRYPOINT ["/app/HgvMate.Mcp"]

