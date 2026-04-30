#!/usr/bin/env bash
# FAULT-001-restore.sh — Restore previous DB password secret version
# Scenario: Invalid Postgres password secret
# Supports: AWS Secrets Manager / Azure Key Vault
set -euo pipefail

# --- Guard: required environment variables ---
: "${FAULT_ENV:?FAULT_ENV is required (e.g., dev, staging)}"
: "${FAULT_REGION:?FAULT_REGION is required (e.g., us-west-2, eastus)}"
: "${FAULT_RESOURCE_PREFIX:?FAULT_RESOURCE_PREFIX is required}"
: "${FAULT_DRY_RUN:=false}"

SECRET_NAME="${FAULT_RESOURCE_PREFIX}-db-password"

echo "[FAULT-001] Restoring DB password secret"
echo "  Environment: ${FAULT_ENV}"
echo "  Region:      ${FAULT_REGION}"
echo "  Secret:      ${SECRET_NAME}"

if [[ "${FAULT_DRY_RUN}" == "true" ]]; then
    echo "[DRY-RUN] Would restore secret '${SECRET_NAME}' to previous version"
    echo "[DRY-RUN] No changes made"
    exit 0
fi

if [[ "${FAULT_REGION}" =~ ^[a-z]+-[a-z]+-[0-9]+$ ]]; then
    echo "[AWS] Finding previous secret version..."
    PREVIOUS_VERSION_ID=$(aws secretsmanager list-secret-version-ids \
        --secret-id "${SECRET_NAME}" \
        --region "${FAULT_REGION}" \
        --query 'Versions[?has(VersionStages, `AWSPREVIOUS`)].VersionId' \
        --output text 2>/dev/null)

    if [[ -z "${PREVIOUS_VERSION_ID}" || "${PREVIOUS_VERSION_ID}" == "None" ]]; then
        echo "[AWS] ERROR: No previous version found for secret '${SECRET_NAME}'"
        exit 1
    fi

    echo "[AWS] Restoring version: ${PREVIOUS_VERSION_ID}"
    PREVIOUS_VALUE=$(aws secretsmanager get-secret-value \
        --secret-id "${SECRET_NAME}" \
        --version-id "${PREVIOUS_VERSION_ID}" \
        --region "${FAULT_REGION}" \
        --query 'SecretString' \
        --output text)

    aws secretsmanager put-secret-value \
        --secret-id "${SECRET_NAME}" \
        --secret-string "${PREVIOUS_VALUE}" \
        --region "${FAULT_REGION}"

    echo "[AWS] Secret restored to previous version."
else
    VAULT_NAME="${FAULT_RESOURCE_PREFIX}-kv"

    echo "[Azure] Finding previous secret version..."
    PREVIOUS_VERSION=$(az keyvault secret list-versions \
        --vault-name "${VAULT_NAME}" \
        --name "${SECRET_NAME}" \
        --query 'sort_by([], &attributes.created)[-2].id' -o tsv 2>/dev/null)

    if [[ -z "${PREVIOUS_VERSION}" ]]; then
        echo "[Azure] ERROR: No previous version found for secret '${SECRET_NAME}'"
        exit 1
    fi

    echo "[Azure] Restoring from version: ${PREVIOUS_VERSION}"
    PREVIOUS_VALUE=$(az keyvault secret show --id "${PREVIOUS_VERSION}" --query 'value' -o tsv)

    az keyvault secret set \
        --vault-name "${VAULT_NAME}" \
        --name "${SECRET_NAME}" \
        --value "${PREVIOUS_VALUE}"

    echo "[Azure] Secret restored to previous version."
fi

echo "[FAULT-001] Restoration complete"
