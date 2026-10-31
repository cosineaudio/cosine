#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet tool restore
dotnet nswag run tools/openapi/nswag.json
