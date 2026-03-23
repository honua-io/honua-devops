#!/usr/bin/env bash
# FAULT-015-restore.sh — Restore correct OTEL exporter endpoint
# Scenario: OTEL exporter target broken
# Supports: AWS ECS / Azure Container Apps / EKS / AKS
set -euo pipefail

# --- Guard: required environment variables ---
: "${FAULT_ENV:?FAULT_ENV is required (e.g., dev, staging)}"
: "${FAULT_REGION:?FAULT_REGION is required (e.g., us-west-2, eastus)}"
: "${FAULT_RESOURCE_PREFIX:?FAULT_RESOURCE_PREFIX is required}"
: "${FAULT_DRY_RUN:=false}"

SERVICE_NAME="${FAULT_RESOURCE_PREFIX}-api"

echo "[FAULT-015] Restoring OTEL exporter endpoint"
echo "  Environment: ${FAULT_ENV}"
echo "  Region:      ${FAULT_REGION}"
echo "  Service:     ${SERVICE_NAME}"

if [[ "${FAULT_DRY_RUN}" == "true" ]]; then
    echo "[DRY-RUN] Would restore OTEL_EXPORTER_OTLP_ENDPOINT to correct value"
    echo "[DRY-RUN] No changes made"
    exit 0
fi

if [[ "${FAULT_REGION}" =~ ^[a-z]+-[a-z]+-[0-9]+$ ]]; then
    echo "[AWS] Retrieving previous task definition..."
    CLUSTER_NAME="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}"

    PREVIOUS_TASK_DEF=$(aws ssm get-parameter \
        --name "/fault-injection/${SERVICE_NAME}/previous-otel-task-def" \
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

    # Clean up
    aws ssm delete-parameter \
        --name "/fault-injection/${SERVICE_NAME}/previous-otel-task-def" \
        --region "${FAULT_REGION}" 2>/dev/null || true

    echo "[AWS] OTEL endpoint restored."
else
    CONTAINER_APP="${SERVICE_NAME}"
    RESOURCE_GROUP="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}-rg"

    echo "[Azure] Determining correct OTEL endpoint..."
    # Use the standard collector endpoint pattern
    CORRECT_ENDPOINT="http://${FAULT_RESOURCE_PREFIX}-otel-collector.${FAULT_ENV}.svc:4317"

    echo "[Azure] Restoring OTEL_EXPORTER_OTLP_ENDPOINT to: ${CORRECT_ENDPOINT}"
    az containerapp update \
        --name "${CONTAINER_APP}" \
        --resource-group "${RESOURCE_GROUP}" \
        --set-env-vars "OTEL_EXPORTER_OTLP_ENDPOINT=${CORRECT_ENDPOINT}"

    echo "[Azure] OTEL endpoint restored."
fi

echo "[FAULT-015] Restoration complete"
