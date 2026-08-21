#!/usr/bin/env bash

set -euo pipefail

REPO="${1:-honua-io/honua-iac}"

if ! command -v gh >/dev/null 2>&1; then
  echo "[ERROR] gh CLI is required" >&2
  exit 1
fi

if ! gh auth status >/dev/null 2>&1; then
  echo "[ERROR] gh CLI is not authenticated" >&2
  exit 1
fi

required_secrets=(
  ARM_CLIENT_ID
  ARM_CLIENT_SECRET
  ARM_TENANT_ID
  ARM_SUBSCRIPTION_ID
  AWS_ACCESS_KEY_ID
  AWS_SECRET_ACCESS_KEY
  HONUA_ADMIN_PASSWORD
  HONUA_DB_PASSWORD
)

optional_secrets=(
  AWS_SESSION_TOKEN
)

present_secrets="$(gh secret list --repo "$REPO" | awk '{print $1}')"

declare -A repo_vars=()
while IFS=$'\t' read -r name value; do
  [[ -n "${name:-}" ]] || continue
  repo_vars["$name"]="$value"
done < <(gh variable list --repo "$REPO" --json name,value --jq '.[] | [.name, .value] | @tsv')

has_secret() {
  local key="$1"
  grep -qx "$key" <<< "$present_secrets"
}

var_value() {
  local key="$1"
  printf '%s' "${repo_vars[$key]:-}"
}

has_var() {
  local key="$1"
  [[ -n "$(var_value "$key")" ]]
}

aws_stack="$(var_value HONUA_AWS_VALIDATION_STACK)"
azure_stack="$(var_value HONUA_AZURE_VALIDATION_STACK)"
run_upgrade_rollback="$(var_value HONUA_RUN_UPGRADE_ROLLBACK)"

if [[ -z "$aws_stack" ]]; then
  aws_stack="both"
fi

if [[ -z "$azure_stack" ]]; then
  azure_stack="both"
fi

if [[ -z "$run_upgrade_rollback" ]]; then
  run_upgrade_rollback="false"
fi

required_vars=()

case "$aws_stack" in
  both)
    required_vars+=(HONUA_AWS_ECS_IMAGE HONUA_AWS_SERVERLESS_IMAGE)
    if [[ "$run_upgrade_rollback" == "true" ]]; then
      required_vars+=(HONUA_AWS_ECS_PREVIOUS_IMAGE HONUA_AWS_SERVERLESS_PREVIOUS_IMAGE)
    fi
    ;;
  ecs)
    required_vars+=(HONUA_AWS_ECS_IMAGE)
    if [[ "$run_upgrade_rollback" == "true" ]]; then
      required_vars+=(HONUA_AWS_ECS_PREVIOUS_IMAGE)
    fi
    ;;
  serverless)
    required_vars+=(HONUA_AWS_SERVERLESS_IMAGE)
    if [[ "$run_upgrade_rollback" == "true" ]]; then
      required_vars+=(HONUA_AWS_SERVERLESS_PREVIOUS_IMAGE)
    fi
    ;;
  *)
    echo "[ERROR] Invalid HONUA_AWS_VALIDATION_STACK value: $aws_stack" >&2
    echo "Allowed values: both, ecs, serverless" >&2
    exit 2
    ;;
esac

case "$azure_stack" in
  both)
    required_vars+=(HONUA_ACA_IMAGE HONUA_FUNCTIONS_IMAGE)
    if [[ "$run_upgrade_rollback" == "true" ]]; then
      required_vars+=(HONUA_ACA_PREVIOUS_IMAGE HONUA_FUNCTIONS_PREVIOUS_IMAGE)
    fi
    ;;
  aca)
    required_vars+=(HONUA_ACA_IMAGE)
    if [[ "$run_upgrade_rollback" == "true" ]]; then
      required_vars+=(HONUA_ACA_PREVIOUS_IMAGE)
    fi
    ;;
  functions)
    required_vars+=(HONUA_FUNCTIONS_IMAGE)
    if [[ "$run_upgrade_rollback" == "true" ]]; then
      required_vars+=(HONUA_FUNCTIONS_PREVIOUS_IMAGE)
    fi
    ;;
  *)
    echo "[ERROR] Invalid HONUA_AZURE_VALIDATION_STACK value: $azure_stack" >&2
    echo "Allowed values: both, aca, functions" >&2
    exit 2
    ;;
esac

recommended_vars=(
  HONUA_K8S_IMAGE
  HONUA_K8S_PREVIOUS_IMAGE
  HONUA_AWS_VALIDATION_REGION
  HONUA_AZURE_VALIDATION_REGION
  HONUA_AWS_ECS_CANARY_IMAGE
)

missing_required_secrets=0
missing_required_vars=0

echo "Repo: $REPO"
echo
echo "Required secrets:"
for key in "${required_secrets[@]}"; do
  if has_secret "$key"; then
    echo "  [OK] $key"
  else
    echo "  [MISSING] $key"
    missing_required_secrets=$((missing_required_secrets + 1))
  fi
done

echo
echo "Optional secrets:"
for key in "${optional_secrets[@]}"; do
  if has_secret "$key"; then
    echo "  [OK] $key"
  else
    echo "  [MISSING] $key"
  fi
done

echo
echo "Effective validation stacks:"
echo "  AWS: $aws_stack"
echo "  Azure: $azure_stack"
echo "  Upgrade/Rollback: $run_upgrade_rollback"

echo
echo "Required repo variables:"
for key in "${required_vars[@]}"; do
  if has_var "$key"; then
    echo "  [OK] $key=$(var_value "$key")"
  else
    echo "  [MISSING] $key"
    missing_required_vars=$((missing_required_vars + 1))
  fi
done

echo
echo "Recommended repo variables:"
for key in "${recommended_vars[@]}"; do
  if has_var "$key"; then
    echo "  [OK] $key=$(var_value "$key")"
  else
    echo "  [MISSING] $key"
  fi
done

if [[ "$missing_required_secrets" -gt 0 ]]; then
  echo
  echo "[RESULT] Missing required secrets: $missing_required_secrets" >&2
  exit 2
fi

if [[ "$missing_required_vars" -gt 0 ]]; then
  echo
  echo "[RESULT] Missing required repo variables: $missing_required_vars" >&2
  exit 2
fi

echo
echo "[RESULT] Required Terraform validation secrets and repo variables are present."
