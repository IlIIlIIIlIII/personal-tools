#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
export LANG="${LANG:-ko_KR.UTF-8}"
exec gjs -m "$ROOT/Sources/Linux/App/app.js" "$@"
