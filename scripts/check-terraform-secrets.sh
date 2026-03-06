#!/usr/bin/env bash

set -euo pipefail

REPO="${1:-honua-io/honua-terraform}"

if ! command -v gh >/dev/null 2>&1; then
  echo "[ERROR] gh CLI is required" >&2
  exit 1
fi

if ! gh auth status >/dev/null 2>&1; then
  echo "[ERROR] gh CLI is not authenticated" >&2
  exit 1
fi

required=(
  ARM_CLIENT_ID
  ARM_CLIENT_SECRET
  ARM_TENANT_ID
  ARM_SUBSCRIPTION_ID
  AWS_ACCESS_KEY_ID
  AWS_SECRET_ACCESS_KEY
  HONUA_ADMIN_PASSWORD
  HONUA_DB_PASSWORD
)

recommended=(
  AWS_SESSION_TOKEN
  HONUA_ACA_IMAGE
  HONUA_ACA_PREVIOUS_IMAGE
  HONUA_FUNCTIONS_IMAGE
  HONUA_FUNCTIONS_PREVIOUS_IMAGE
  HONUA_AWS_ECS_IMAGE
  HONUA_AWS_ECS_PREVIOUS_IMAGE
  HONUA_AWS_SERVERLESS_IMAGE
  HONUA_AWS_SERVERLESS_PREVIOUS_IMAGE
  HONUA_K8S_IMAGE
  HONUA_K8S_PREVIOUS_IMAGE
)

present="$(gh secret list --repo "$REPO" | awk '{print $1}')"

has_secret() {
  local key="$1"
  grep -qx "$key" <<< "$present"
}

missing_required=0
echo "Repo: $REPO"
echo
echo "Required secrets:"
for key in "${required[@]}"; do
  if has_secret "$key"; then
    echo "  [OK] $key"
  else
    echo "  [MISSING] $key"
    missing_required=$((missing_required + 1))
  fi
done

echo
echo "Recommended secrets:"
for key in "${recommended[@]}"; do
  if has_secret "$key"; then
    echo "  [OK] $key"
  else
    echo "  [MISSING] $key"
  fi
done

if [[ "$missing_required" -gt 0 ]]; then
  echo
  echo "[RESULT] Missing required secrets: $missing_required" >&2
  exit 2
fi

echo
echo "[RESULT] Required Terraform validation secrets are present."
