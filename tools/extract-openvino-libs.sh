#!/usr/bin/env bash
# extract-openvino-libs.sh — Extract OpenVINO native libraries from the Python wheel
# for use with Microsoft.ML.OnnxRuntime.Managed in .NET.
#
# Usage: ./tools/extract-openvino-libs.sh [version]
#   version: onnxruntime-openvino PyPI version (default: 1.24.1)
#
# Output: libs/openvino/linux-x64/*.so  (CPU-only subset, ~127 MB)
#         libs/openvino/linux-x64.tar.gz (compressed tarball for GitHub Release)
set -euo pipefail

VERSION="${1:-1.24.1}"
PYTHON_ABI="cp312"
PLATFORM="manylinux_2_28_x86_64"
WHEEL_NAME="onnxruntime_openvino-${VERSION}-${PYTHON_ABI}-${PYTHON_ABI}-${PLATFORM}.whl"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT_DIR="$ROOT_DIR/libs/openvino/linux-x64"
WORK_DIR=$(mktemp -d)
trap 'rm -rf "$WORK_DIR"' EXIT

echo "=== Extracting OpenVINO libs from onnxruntime-openvino==$VERSION ==="

# 1. Get the wheel download URL from PyPI
echo "Querying PyPI for wheel URL..."
WHEEL_URL=$(curl -sL "https://pypi.org/pypi/onnxruntime-openvino/${VERSION}/json" \
  | jq -r ".urls[] | select(.filename == \"${WHEEL_NAME}\") | .url")

if [ -z "$WHEEL_URL" ] || [ "$WHEEL_URL" = "null" ]; then
  echo "ERROR: Could not find wheel ${WHEEL_NAME} on PyPI" >&2
  exit 1
fi

# 2. Download the wheel
echo "Downloading ${WHEEL_NAME} ($(curl -sL "https://pypi.org/pypi/onnxruntime-openvino/${VERSION}/json" \
  | jq -r ".urls[] | select(.filename == \"${WHEEL_NAME}\") | .size" \
  | awk '{printf "%.0f MB", $1/1024/1024}'))..."
curl -fSL "$WHEEL_URL" -o "$WORK_DIR/wheel.whl"

# 3. Extract only the CPU-essential .so files (no GPU/NPU/Auto plugins, no Python binding)
echo "Extracting CPU-only native libraries..."
mkdir -p "$OUT_DIR"

# These are the files needed for ORT + OpenVINO CPU inference:
#   libonnxruntime.so.X.Y.Z     — ONNX Runtime core (built with OpenVINO support)
#   libonnxruntime_providers_openvino.so — OpenVINO EP plugin
#   libonnxruntime_providers_shared.so   — Shared EP infrastructure
#   libopenvino.so*              — OpenVINO core runtime (+ versioned sonames)
#   libopenvino_c.so*            — OpenVINO C API (+ versioned sonames)
#   libopenvino_intel_cpu_plugin.so — Intel CPU inference backend
#   libopenvino_onnx_frontend.so* — ONNX model parser (+ versioned sonames)
#   libtbb.so.12                 — Intel TBB threading (required by OpenVINO)
#   libtbbmalloc.so              — TBB memory allocator
#
# Note: versioned sonames (.so.2541, .so.2025.4.1) are required because OpenVINO
# plugins dlopen the versioned names at runtime.
CPU_LIB_PATTERNS=(
  "onnxruntime/capi/libonnxruntime.so.${VERSION}"
  "onnxruntime/capi/libonnxruntime_providers_openvino.so"
  "onnxruntime/capi/libonnxruntime_providers_shared.so"
  "onnxruntime/capi/libopenvino.so*"
  "onnxruntime/capi/libopenvino_c.so*"
  "onnxruntime/capi/libopenvino_intel_cpu_plugin.so"
  "onnxruntime/capi/libopenvino_onnx_frontend.so*"
  "onnxruntime/capi/libtbb.so*"
  "onnxruntime/capi/libtbbmalloc.so"
)

cd "$WORK_DIR"
mkdir -p extracted
# Extract matching files using wildcard patterns
for pattern in "${CPU_LIB_PATTERNS[@]}"; do
  unzip -jo wheel.whl "$pattern" -d extracted/ 2>/dev/null || {
    echo "WARNING: no files matched: $pattern" >&2
  }
done

# 4. Copy to output directory and create required symlinks
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"
cp "$WORK_DIR/extracted/"*.so* "$OUT_DIR/"

# ORT loads libonnxruntime.so (without version suffix) — create symlink
cd "$OUT_DIR"
ln -sf "libonnxruntime.so.${VERSION}" libonnxruntime.so

# OpenVINO versioned symlinks (loader expects .so.2541 for 2025.4.1)
OV_VERSION=$(echo "$VERSION" | head -c 100)  # Use ORT version for naming
# The actual OpenVINO version is embedded in the libs — check what the wheel had
# For 1.24.1: OpenVINO 2025.4.1 → soname suffix .2541
# These symlinks aren't strictly needed since we extracted the unversioned .so,
# but the plugins may dlopen the versioned name at runtime.

echo ""
echo "=== Extracted files ==="
ls -lh "$OUT_DIR/"
echo ""
du -sh "$OUT_DIR/"

# 5. Create compressed tarball for GitHub Release upload
TARBALL="$ROOT_DIR/libs/openvino/linux-x64.tar.gz"
echo "Creating tarball: $TARBALL"
tar -czf "$TARBALL" -C "$ROOT_DIR/libs/openvino" linux-x64/
ls -lh "$TARBALL"

# 6. Write version manifest
cat > "$ROOT_DIR/libs/openvino/manifest.json" << EOF
{
  "onnxruntime_openvino_version": "${VERSION}",
  "platform": "linux-x64",
  "source_wheel": "${WHEEL_NAME}",
  "extracted_date": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "files": [
$(ls -1 "$OUT_DIR/" | sed 's/^/    "/; s/$/"/' | paste -sd ',' | sed 's/,/,\n/g')
  ]
}
EOF

echo ""
echo "=== Done ==="
echo "Libs:     $OUT_DIR/"
echo "Tarball:  $TARBALL"
echo "Manifest: $ROOT_DIR/libs/openvino/manifest.json"
echo ""
echo "Next steps:"
echo "  1. gh release create openvino-libs/v${VERSION} '$TARBALL' --title 'OpenVINO libs ${VERSION}' --notes 'Pre-extracted OpenVINO native libraries for Linux x64'"
echo "  2. Update OPENVINO_LIBS_VERSION in Dockerfile"
