#!/usr/bin/env bash
# FAULT-009-restore.sh — Restore correct OIDC issuer env var
# Scenario: Broken OIDC issuer or audience config
# Supports: AWS ECS / Azure Container Apps / EKS / AKS
set -euo pipefail

# --- Guard: required environment variables ---
: "${FAULT_ENV:?FAULT_ENV is required (e.g., dev, staging)}"
: "${FAULT_REGION:?FAULT_REGION is required (e.g., us-west-2, eastus)}"
: "${FAULT_RESOURCE_PREFIX:?FAULT_RESOURCE_PREFIX is required}"
: "${FAULT_DRY_RUN:=false}"

SERVICE_NAME="${FAULT_RESOURCE_PREFIX}-api"

echo "[FAULT-009] Restoring OIDC issuer"
echo "  Environment: ${FAULT_ENV}"
echo "  Region:      ${FAULT_REGION}"
echo "  Service:     ${SERVICE_NAME}"

if [[ "${FAULT_DRY_RUN}" == "true" ]]; then
    echo "[DRY-RUN] Would restore OIDC_ISSUER to previous value on service '${SERVICE_NAME}'"
    echo "[DRY-RUN] No changes made"
    exit 0
fi

if [[ "${FAULT_REGION}" =~ ^[a-z]+-[a-z]+-[0-9]+$ ]]; then
    echo "[AWS] Retrieving previous task definition..."
    CLUSTER_NAME="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}"

    PREVIOUS_TASK_DEF=$(aws ssm get-parameter \
        --name "/fault-injection/${SERVICE_NAME}/previous-task-def" \
        --region "${FAULT_REGION}" \
        --query 'Parameter.Value' \
        --output text 2>/dev/null)

    if [[ -z "${PREVIOUS_TASK_DEF}" ]]; then
        echo "[AWS] ERROR: No previous task definition found for rollback"
        exit 1
    fi

    echo "[AWS] Rolling back to task definition: ${PREVIOUS_TASK_DEF}"
    aws ecs update-service \
        --cluster "${CLUSTER_NAME}" \
        --service "${SERVICE_NAME}" \
        --task-definition "${PREVIOUS_TASK_DEF}" \
        --region "${FAULT_REGION}"

    echo "[AWS] Waiting for service to stabilize..."
    aws ecs wait services-stable \
        --cluster "${CLUSTER_NAME}" \
        --services "${SERVICE_NAME}" \
        --region "${FAULT_REGION}" || echo "[AWS] Warning: service did not stabilize within timeout"

    # Clean up the stored parameter
    aws ssm delete-parameter \
        --name "/fault-injection/${SERVICE_NAME}/previous-task-def" \
        --region "${FAULT_REGION}" 2>/dev/null || true

    echo "[AWS] OIDC issuer restored."
else
    CONTAINER_APP="${SERVICE_NAME}"
    RESOURCE_GROUP="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}-rg"

    echo "[Azure] Retrieving correct OIDC issuer from desired-state config..."
    CORRECT_ISSUER=$(az containerapp show \
        --name "${CONTAINER_APP}" \
        --resource-group "${RESOURCE_GROUP}" \
        --query 'properties.configuration.ingress.fqdn' \
        -o tsv 2>/dev/null || echo "")

    # Fall back to well-known issuer pattern
    if [[ -z "${CORRECT_ISSUER}" ]]; then
        CORRECT_ISSUER="https://login.microsoftonline.com/${FAULT_RESOURCE_PREFIX}/v2.0"
    fi

    echo "[Azure] Restoring OIDC_ISSUER to: ${CORRECT_ISSUER}"
    az containerapp update \
        --name "${CONTAINER_APP}" \
        --resource-group "${RESOURCE_GROUP}" \
        --set-env-vars "OIDC_ISSUER=${CORRECT_ISSUER}"

    echo "[Azure] OIDC issuer restored."
fi

echo "[FAULT-009] Restoration complete"
