#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
PROJECT="${REPO_ROOT}/benchmarks/UltraCompress.Benchmarks/UltraCompress.Benchmarks.csproj"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "Error: .NET 8 SDK ('dotnet') is required on PATH." >&2
  exit 1
fi

echo "[UltraCompress Benchmarks] Building Release harness..."
dotnet build "${PROJECT}" -c Release --nologo -v quiet

echo "[UltraCompress Benchmarks] Executing benchmark suite..."
dotnet run -c Release --no-build --project "${PROJECT}" -- "$@"
