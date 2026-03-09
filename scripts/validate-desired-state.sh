#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

DESIRED_STATE_ROOT="$REPO_ROOT/desired-state"
CONFIGURATION="Debug"

usage() {
  cat <<'EOF'
Usage:
  scripts/validate-desired-state.sh [options]

Options:
  --root <path>                 Desired-state root to validate. Default: repo desired-state/
  --configuration <value>       dotnet test configuration. Default: Debug
  --help                        Show help
EOF
}

require_value() {
  local flag="$1"
  local value="${2:-}"
  if [[ -z "$value" ]]; then
    echo "[ERROR] $flag requires a value" >&2
    exit 1
  fi
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --root)
      require_value "$1" "${2:-}"
      DESIRED_STATE_ROOT="$2"
      shift 2
      ;;
    --configuration)
      require_value "$1" "${2:-}"
      CONFIGURATION="$2"
      shift 2
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      echo "[ERROR] Unknown arg: $1" >&2
      usage
      exit 1
      ;;
  esac
done

case "$DESIRED_STATE_ROOT" in
  /*) ;;
  *) DESIRED_STATE_ROOT="$REPO_ROOT/$DESIRED_STATE_ROOT" ;;
esac

if [[ ! -d "$DESIRED_STATE_ROOT" ]]; then
  echo "[ERROR] desired-state root not found: $DESIRED_STATE_ROOT" >&2
  exit 1
fi

echo "Validating desired-state root: $DESIRED_STATE_ROOT"

HONUA_DEVOPS_DESIRED_STATE_ROOT="$DESIRED_STATE_ROOT" \
dotnet test "$REPO_ROOT/tests/Honua.DevOps.Agent.Tests/Honua.DevOps.Agent.Tests.csproj" \
  --configuration "$CONFIGURATION" \
  --filter "FullyQualifiedName~DesiredStateValidationTests"
