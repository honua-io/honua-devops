#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
CONVENTIONS_FILE="$REPO_ROOT/desired-state/conventions.env"

# shellcheck disable=SC1090
source "$CONVENTIONS_FILE"

RUNTIME_TARGET="${ALLOWED_RUNTIME_TARGETS%%,*}"
WORKDIR="$(mktemp -d)"
OUTPUT_ROOT="$WORKDIR/desired-state"

cleanup() {
  rm -rf "$WORKDIR"
}

trap cleanup EXIT

echo "Scaffolding desired-state smoke tree into: $OUTPUT_ROOT"

"$REPO_ROOT/scripts/scaffold-desired-state.sh" \
  --service scaffold-smoke-api \
  --runtime-target "$RUNTIME_TARGET" \
  --revision release/ci-smoke.001 \
  --output-root "$OUTPUT_ROOT" \
  --force

echo "Validating scaffolded desired-state smoke tree"

"$REPO_ROOT/scripts/validate-desired-state.sh" \
  --root "$OUTPUT_ROOT" \
  --configuration Release

echo "Desired-state scaffold smoke check passed."
