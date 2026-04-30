#!/usr/bin/env bash
# FAULT-009-verify-restored.sh - Verify OIDC issuer is restored
set -euo pipefail

: "${FAULT_ENV:?FAULT_ENV is required (e.g., dev, staging)}"
: "${FAULT_REGION:?FAULT_REGION is required (e.g., us-west-2, eastus)}"
: "${FAULT_RESOURCE_PREFIX:?FAULT_RESOURCE_PREFIX is required}"
: "${FAULT_DRY_RUN:=false}"

SERVICE_NAME="${FAULT_RESOURCE_PREFIX}-api"
INVALID_ISSUER="https://invalid-oidc-issuer.fault-injection.local"

echo "[FAULT-009] Verifying OIDC issuer is restored"
echo "  Environment: ${FAULT_ENV}"
echo "  Region:      ${FAULT_REGION}"
echo "  Service:     ${SERVICE_NAME}"

if [[ "${FAULT_DRY_RUN}" == "true" ]]; then
    echo "[DRY-RUN] Would verify OIDC_ISSUER no longer equals '${INVALID_ISSUER}'"
    exit 0
fi

if [[ "${FAULT_REGION}" =~ ^[a-z]+-[a-z]+-[0-9]+$ ]]; then
    CLUSTER_NAME="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}"
    TASK_DEF=$(aws ecs describe-services \
        --cluster "${CLUSTER_NAME}" \
        --services "${SERVICE_NAME}" \
        --region "${FAULT_REGION}" \
        --query 'services[0].taskDefinition' \
        --output text)
    CURRENT_ISSUER=$(aws ecs describe-task-definition \
        --task-definition "${TASK_DEF}" \
        --region "${FAULT_REGION}" \
        --query 'taskDefinition.containerDefinitions[0].environment[?name==`OIDC_ISSUER`].value | [0]' \
        --output text)
else
    CONTAINER_APP="${SERVICE_NAME}"
    RESOURCE_GROUP="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}-rg"
    CURRENT_ISSUER=$(az containerapp show \
        --name "${CONTAINER_APP}" \
        --resource-group "${RESOURCE_GROUP}" \
        --query 'properties.template.containers[0].env[?name==`OIDC_ISSUER`].value' \
        -o tsv)
fi

if [[ -n "${CURRENT_ISSUER}" && "${CURRENT_ISSUER}" != "${INVALID_ISSUER}" && "${CURRENT_ISSUER}" != "None" ]]; then
    echo "[FAULT-009] Verified OIDC issuer is restored: ${CURRENT_ISSUER}"
    exit 0
fi

echo "[FAULT-009] ERROR: OIDC_ISSUER is not restored (value: '${CURRENT_ISSUER}')" >&2
exit 1
