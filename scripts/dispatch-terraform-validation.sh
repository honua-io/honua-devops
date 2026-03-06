#!/usr/bin/env bash

set -euo pipefail

REPO="honua-io/honua-terraform"
CLOUD="both"
PROFILE="ephemeral"
APPLY_CONFIRMATION=""
RUN_LIVE="true"
RUN_K8S="true"
RUN_AKS="false"
RUN_EKS="false"
RUN_DRIFT="false"
NO_DESTROY="false"
ALLOW_DESTROY_PLAN="false"

usage() {
  cat <<'EOF'
Usage:
  scripts/dispatch-terraform-validation.sh [options]

Options:
  --cloud <both|azure|aws>               Default: both
  --profile <ephemeral|persistent>       Default: ephemeral
  --apply-confirmation <APPROVED>        Required by workflow when profile=persistent
  --run-live <true|false>                Default: true
  --run-k8s <true|false>                 Default: true
  --run-aks <true|false>                 Default: false
  --run-eks <true|false>                 Default: false
  --run-drift <true|false>               Default: false
  --no-destroy <true|false>              Default: false
  --allow-destroy-plan <true|false>      Default: false
  --help                                 Show help
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --cloud) CLOUD="$2"; shift 2 ;;
    --profile) PROFILE="$2"; shift 2 ;;
    --apply-confirmation) APPLY_CONFIRMATION="$2"; shift 2 ;;
    --run-live) RUN_LIVE="$2"; shift 2 ;;
    --run-k8s) RUN_K8S="$2"; shift 2 ;;
    --run-aks) RUN_AKS="$2"; shift 2 ;;
    --run-eks) RUN_EKS="$2"; shift 2 ;;
    --run-drift) RUN_DRIFT="$2"; shift 2 ;;
    --no-destroy) NO_DESTROY="$2"; shift 2 ;;
    --allow-destroy-plan) ALLOW_DESTROY_PLAN="$2"; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) echo "Unknown arg: $1" >&2; usage; exit 1 ;;
  esac
done

if ! command -v gh >/dev/null 2>&1; then
  echo "[ERROR] gh CLI is required" >&2
  exit 1
fi

gh workflow run terraform-manual-validation.yml \
  --repo "$REPO" \
  -f cloud="$CLOUD" \
  -f deployment_profile="$PROFILE" \
  -f apply_confirmation="$APPLY_CONFIRMATION" \
  -f run_live="$RUN_LIVE" \
  -f run_k8s="$RUN_K8S" \
  -f run_aks="$RUN_AKS" \
  -f run_eks="$RUN_EKS" \
  -f run_drift="$RUN_DRIFT" \
  -f no_destroy="$NO_DESTROY" \
  -f allow_destroy_plan="$ALLOW_DESTROY_PLAN"

echo
echo "Dispatched terraform-manual-validation.yml in $REPO"
echo "Recent runs:"
gh run list --repo "$REPO" --workflow terraform-manual-validation.yml --limit 5
