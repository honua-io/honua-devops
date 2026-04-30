#!/usr/bin/env bash
# FAULT-010-verify-restored.sh - Verify deployment rollout is healthy again
set -euo pipefail

: "${FAULT_ENV:?FAULT_ENV is required (e.g., dev, staging)}"
: "${FAULT_REGION:?FAULT_REGION is required (e.g., us-west-2, eastus)}"
: "${FAULT_RESOURCE_PREFIX:?FAULT_RESOURCE_PREFIX is required}"
: "${FAULT_DRY_RUN:=false}"

NAMESPACE="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}"
DEPLOYMENT_NAME="${FAULT_RESOURCE_PREFIX}-api"

echo "[FAULT-010] Verifying deployment restoration"
echo "  Environment: ${FAULT_ENV}"
echo "  Region:      ${FAULT_REGION}"
echo "  Namespace:   ${NAMESPACE}"
echo "  Deployment:  ${DEPLOYMENT_NAME}"

if [[ "${FAULT_DRY_RUN}" == "true" ]]; then
    echo "[DRY-RUN] Would verify deployment rollout is healthy and bad image marker is gone"
    exit 0
fi

kubectl rollout status deployment/"${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    --timeout=60s

CURRENT_IMAGE=$(kubectl get deployment "${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    -o jsonpath='{.spec.template.spec.containers[0].image}')

if [[ "${CURRENT_IMAGE}" == *"nonexistent-fault-injection"* ]]; then
    echo "[FAULT-010] ERROR: deployment still references bad image '${CURRENT_IMAGE}'" >&2
    exit 1
fi

echo "[FAULT-010] Verified deployment is restored with image: ${CURRENT_IMAGE}"
