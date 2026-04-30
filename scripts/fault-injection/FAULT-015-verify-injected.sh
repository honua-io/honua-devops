#!/usr/bin/env bash
# FAULT-015-verify-injected.sh - Verify invalid OTEL exporter endpoint is active
set -euo pipefail

: "${FAULT_ENV:?FAULT_ENV is required (e.g., dev, staging)}"
: "${FAULT_REGION:?FAULT_REGION is required (e.g., us-west-2, eastus)}"
: "${FAULT_RESOURCE_PREFIX:?FAULT_RESOURCE_PREFIX is required}"
: "${FAULT_DRY_RUN:=false}"

SERVICE_NAME="${FAULT_RESOURCE_PREFIX}-api"
INVALID_OTEL_ENDPOINT="http://invalid-otel-collector.fault-injection.local:4317"

echo "[FAULT-015] Verifying invalid OTEL exporter endpoint"
echo "  Environment: ${FAULT_ENV}"
echo "  Region:      ${FAULT_REGION}"
echo "  Service:     ${SERVICE_NAME}"

if [[ "${FAULT_DRY_RUN}" == "true" ]]; then
    echo "[DRY-RUN] Would verify OTEL_EXPORTER_OTLP_ENDPOINT equals '${INVALID_OTEL_ENDPOINT}'"
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
    CURRENT_ENDPOINT=$(aws ecs describe-task-definition \
        --task-definition "${TASK_DEF}" \
        --region "${FAULT_REGION}" \
        --query 'taskDefinition.containerDefinitions[0].environment[?name==`OTEL_EXPORTER_OTLP_ENDPOINT`].value | [0]' \
        --output text)
else
    CONTAINER_APP="${SERVICE_NAME}"
    RESOURCE_GROUP="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}-rg"
    CURRENT_ENDPOINT=$(az containerapp show \
        --name "${CONTAINER_APP}" \
        --resource-group "${RESOURCE_GROUP}" \
        --query 'properties.template.containers[0].env[?name==`OTEL_EXPORTER_OTLP_ENDPOINT`].value' \
        -o tsv)
fi

if [[ "${CURRENT_ENDPOINT}" == "${INVALID_OTEL_ENDPOINT}" ]]; then
    echo "[FAULT-015] Verified invalid OTEL endpoint is active"
    exit 0
fi

echo "[FAULT-015] ERROR: OTEL endpoint is '${CURRENT_ENDPOINT}', expected '${INVALID_OTEL_ENDPOINT}'" >&2
exit 1
