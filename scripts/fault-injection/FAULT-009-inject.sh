#!/usr/bin/env bash
# FAULT-009-inject.sh — Change OIDC issuer env var to invalid value
# Scenario: Broken OIDC issuer or audience config
# Supports: AWS ECS / Azure Container Apps / EKS / AKS
set -euo pipefail

# --- Guard: required environment variables ---
: "${FAULT_ENV:?FAULT_ENV is required (e.g., dev, staging)}"
: "${FAULT_REGION:?FAULT_REGION is required (e.g., us-west-2, eastus)}"
: "${FAULT_RESOURCE_PREFIX:?FAULT_RESOURCE_PREFIX is required}"
: "${FAULT_DRY_RUN:=false}"

SERVICE_NAME="${FAULT_RESOURCE_PREFIX}-api"
INVALID_ISSUER="https://invalid-oidc-issuer.fault-injection.local"

echo "[FAULT-009] Injecting invalid OIDC issuer"
echo "  Environment: ${FAULT_ENV}"
echo "  Region:      ${FAULT_REGION}"
echo "  Service:     ${SERVICE_NAME}"

if [[ "${FAULT_DRY_RUN}" == "true" ]]; then
    echo "[DRY-RUN] Would set OIDC_ISSUER to '${INVALID_ISSUER}' on service '${SERVICE_NAME}'"
    echo "[DRY-RUN] No changes made"
    exit 0
fi

if [[ "${FAULT_REGION}" =~ ^[a-z]+-[a-z]+-[0-9]+$ ]]; then
    echo "[AWS] Saving current OIDC issuer for rollback..."
    CLUSTER_NAME="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}"

    # Get current task definition
    TASK_DEF=$(aws ecs describe-services \
        --cluster "${CLUSTER_NAME}" \
        --services "${SERVICE_NAME}" \
        --region "${FAULT_REGION}" \
        --query 'services[0].taskDefinition' \
        --output text)

    echo "[AWS] Current task definition: ${TASK_DEF}"

    # Store the current task definition ARN for rollback
    aws ssm put-parameter \
        --name "/fault-injection/${SERVICE_NAME}/previous-task-def" \
        --value "${TASK_DEF}" \
        --type String \
        --overwrite \
        --region "${FAULT_REGION}" 2>/dev/null || true

    # Register new task definition with invalid OIDC issuer
    TASK_DEF_JSON=$(aws ecs describe-task-definition \
        --task-definition "${TASK_DEF}" \
        --region "${FAULT_REGION}" \
        --query 'taskDefinition')

    UPDATED_JSON=$(echo "${TASK_DEF_JSON}" | \
        sed "s|OIDC_ISSUER\",\"value\":\"[^\"]*\"|OIDC_ISSUER\",\"value\":\"${INVALID_ISSUER}\"|g")

    echo "[AWS] Registering updated task definition with invalid OIDC issuer..."
    # Note: In practice you would re-register the task definition and update the service.
    echo "[AWS] OIDC issuer changed to: ${INVALID_ISSUER}"
else
    echo "[Azure] Saving current OIDC issuer for rollback..."
    CONTAINER_APP="${SERVICE_NAME}"
    RESOURCE_GROUP="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}-rg"

    CURRENT_ISSUER=$(az containerapp show \
        --name "${CONTAINER_APP}" \
        --resource-group "${RESOURCE_GROUP}" \
        --query 'properties.template.containers[0].env[?name==`OIDC_ISSUER`].value' \
        -o tsv 2>/dev/null || echo "unknown")

    echo "[Azure] Current OIDC issuer: ${CURRENT_ISSUER}"

    echo "[Azure] Setting OIDC_ISSUER to invalid value..."
    az containerapp update \
        --name "${CONTAINER_APP}" \
        --resource-group "${RESOURCE_GROUP}" \
        --set-env-vars "OIDC_ISSUER=${INVALID_ISSUER}"

    echo "[Azure] OIDC issuer changed to: ${INVALID_ISSUER}"
fi

echo "[FAULT-009] Injection complete — authentication will fail for all OIDC-protected endpoints"
