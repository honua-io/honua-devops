#!/usr/bin/env bash
# FAULT-001-inject.sh — Rotate DB password to invalid value
# Scenario: Invalid Postgres password secret
# Supports: AWS Secrets Manager / Azure Key Vault
set -euo pipefail

# --- Guard: required environment variables ---
: "${FAULT_ENV:?FAULT_ENV is required (e.g., dev, staging)}"
: "${FAULT_REGION:?FAULT_REGION is required (e.g., us-west-2, eastus)}"
: "${FAULT_RESOURCE_PREFIX:?FAULT_RESOURCE_PREFIX is required}"
: "${FAULT_DRY_RUN:=false}"

SECRET_NAME="${FAULT_RESOURCE_PREFIX}-db-password"
INVALID_PASSWORD="fault-injection-invalid-$(date +%s)"

echo "[FAULT-001] Injecting invalid DB password"
echo "  Environment: ${FAULT_ENV}"
echo "  Region:      ${FAULT_REGION}"
echo "  Secret:      ${SECRET_NAME}"

if [[ "${FAULT_DRY_RUN}" == "true" ]]; then
    echo "[DRY-RUN] Would rotate secret '${SECRET_NAME}' to invalid value"
    echo "[DRY-RUN] No changes made"
    exit 0
fi

# Detect cloud provider based on available CLI tools and region format
if [[ "${FAULT_REGION}" =~ ^[a-z]+-[a-z]+-[0-9]+$ ]]; then
    echo "[AWS] Storing previous secret version for rollback..."
    PREVIOUS_VERSION=$(aws secretsmanager describe-secret \
        --secret-id "${SECRET_NAME}" \
        --region "${FAULT_REGION}" \
        --query 'VersionIdsToStages' \
        --output text 2>/dev/null || echo "unknown")
    echo "[AWS] Previous version info: ${PREVIOUS_VERSION}"

    echo "[AWS] Rotating secret to invalid value..."
    aws secretsmanager put-secret-value \
        --secret-id "${SECRET_NAME}" \
        --secret-string "${INVALID_PASSWORD}" \
        --region "${FAULT_REGION}"

    echo "[AWS] Secret rotated. Services using this secret will fail on next connection."
else
    echo "[Azure] Storing previous secret version for rollback..."
    VAULT_NAME="${FAULT_RESOURCE_PREFIX}-kv"

    PREVIOUS_VERSION=$(az keyvault secret show \
        --vault-name "${VAULT_NAME}" \
        --name "${SECRET_NAME}" \
        --query 'id' -o tsv 2>/dev/null || echo "unknown")
    echo "[Azure] Previous version: ${PREVIOUS_VERSION}"

    echo "[Azure] Setting secret to invalid value..."
    az keyvault secret set \
        --vault-name "${VAULT_NAME}" \
        --name "${SECRET_NAME}" \
        --value "${INVALID_PASSWORD}"

    echo "[Azure] Secret updated. Services using this secret will fail on next connection."
fi

echo "[FAULT-001] Injection complete"
