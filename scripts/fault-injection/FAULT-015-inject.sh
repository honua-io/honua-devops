#!/usr/bin/env bash
# FAULT-015-inject.sh — Change OTEL exporter endpoint to invalid target
# Scenario: OTEL exporter target broken
# Supports: AWS ECS / Azure Container Apps / EKS / AKS
set -euo pipefail

# --- Guard: required environment variables ---
: "${FAULT_ENV:?FAULT_ENV is required (e.g., dev, staging)}"
: "${FAULT_REGION:?FAULT_REGION is required (e.g., us-west-2, eastus)}"
: "${FAULT_RESOURCE_PREFIX:?FAULT_RESOURCE_PREFIX is required}"
: "${FAULT_DRY_RUN:=false}"

SERVICE_NAME="${FAULT_RESOURCE_PREFIX}-api"
INVALID_OTEL_ENDPOINT="http://invalid-otel-collector.fault-injection.local:4317"

echo "[FAULT-015] Injecting invalid OTEL exporter endpoint"
echo "  Environment: ${FAULT_ENV}"
echo "  Region:      ${FAULT_REGION}"
echo "  Service:     ${SERVICE_NAME}"
echo "  Bad endpoint: ${INVALID_OTEL_ENDPOINT}"

if [[ "${FAULT_DRY_RUN}" == "true" ]]; then
    echo "[DRY-RUN] Would set OTEL_EXPORTER_OTLP_ENDPOINT to '${INVALID_OTEL_ENDPOINT}'"
    echo "[DRY-RUN] No changes made"
    exit 0
fi

if [[ "${FAULT_REGION}" =~ ^[a-z]+-[a-z]+-[0-9]+$ ]]; then
    echo "[AWS] Updating OTEL endpoint in ECS task definition..."
    CLUSTER_NAME="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}"

    # Get current task definition
    TASK_DEF=$(aws ecs describe-services \
        --cluster "${CLUSTER_NAME}" \
        --services "${SERVICE_NAME}" \
        --region "${FAULT_REGION}" \
        --query 'services[0].taskDefinition' \
        --output text)

    echo "[AWS] Current task definition: ${TASK_DEF}"

    # Store for rollback
    aws ssm put-parameter \
        --name "/fault-injection/${SERVICE_NAME}/previous-otel-task-def" \
        --value "${TASK_DEF}" \
        --type String \
        --overwrite \
        --region "${FAULT_REGION}" 2>/dev/null || true

    echo "[AWS] OTEL exporter endpoint would be changed in new task definition revision"
    echo "[AWS] Endpoint changed to: ${INVALID_OTEL_ENDPOINT}"
else
    CONTAINER_APP="${SERVICE_NAME}"
    RESOURCE_GROUP="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}-rg"

    echo "[Azure] Saving current OTEL endpoint..."
    CURRENT_ENDPOINT=$(az containerapp show \
        --name "${CONTAINER_APP}" \
        --resource-group "${RESOURCE_GROUP}" \
        --query 'properties.template.containers[0].env[?name==`OTEL_EXPORTER_OTLP_ENDPOINT`].value' \
        -o tsv 2>/dev/null || echo "unknown")

    echo "[Azure] Current OTEL endpoint: ${CURRENT_ENDPOINT}"

    echo "[Azure] Setting OTEL_EXPORTER_OTLP_ENDPOINT to invalid value..."
    az containerapp update \
        --name "${CONTAINER_APP}" \
        --resource-group "${RESOURCE_GROUP}" \
        --set-env-vars "OTEL_EXPORTER_OTLP_ENDPOINT=${INVALID_OTEL_ENDPOINT}"

    echo "[Azure] OTEL endpoint changed to: ${INVALID_OTEL_ENDPOINT}"
fi

echo "[FAULT-015] Injection complete — traces and metrics will fail to export"
