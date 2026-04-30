#!/usr/bin/env bash
# FAULT-001-verify-restored.sh - Verify DB password secret is restored
set -euo pipefail

: "${FAULT_ENV:?FAULT_ENV is required (e.g., dev, staging)}"
: "${FAULT_REGION:?FAULT_REGION is required (e.g., us-west-2, eastus)}"
: "${FAULT_RESOURCE_PREFIX:?FAULT_RESOURCE_PREFIX is required}"
: "${FAULT_DRY_RUN:=false}"

SECRET_NAME="${FAULT_RESOURCE_PREFIX}-db-password"

echo "[FAULT-001] Verifying DB password secret is restored"
echo "  Environment: ${FAULT_ENV}"
echo "  Region:      ${FAULT_REGION}"
echo "  Secret:      ${SECRET_NAME}"

if [[ "${FAULT_DRY_RUN}" == "true" ]]; then
    echo "[DRY-RUN] Would verify secret '${SECRET_NAME}' no longer starts with fault-injection-invalid-"
    exit 0
fi

if [[ "${FAULT_REGION}" =~ ^[a-z]+-[a-z]+-[0-9]+$ ]]; then
    CURRENT_VALUE=$(aws secretsmanager get-secret-value \
        --secret-id "${SECRET_NAME}" \
        --region "${FAULT_REGION}" \
        --query 'SecretString' \
        --output text)
else
    VAULT_NAME="${FAULT_RESOURCE_PREFIX}-kv"
    CURRENT_VALUE=$(az keyvault secret show \
        --vault-name "${VAULT_NAME}" \
        --name "${SECRET_NAME}" \
        --query 'value' -o tsv)
fi

if [[ "${CURRENT_VALUE}" == fault-injection-invalid-* ]]; then
    echo "[FAULT-001] ERROR: secret '${SECRET_NAME}' still contains the injected invalid value" >&2
    exit 1
fi

echo "[FAULT-001] Verified secret is restored"
