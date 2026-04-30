#!/usr/bin/env bash
# FAULT-016-verify-injected.sh - Verify manual GitOps drift is active
set -euo pipefail

: "${FAULT_ENV:?FAULT_ENV is required (e.g., dev, staging)}"
: "${FAULT_REGION:?FAULT_REGION is required (e.g., us-west-2, eastus)}"
: "${FAULT_RESOURCE_PREFIX:?FAULT_RESOURCE_PREFIX is required}"
: "${FAULT_DRY_RUN:=false}"

NAMESPACE="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}"
DEPLOYMENT_NAME="${FAULT_RESOURCE_PREFIX}-api"

echo "[FAULT-016] Verifying manual GitOps drift"
echo "  Environment: ${FAULT_ENV}"
echo "  Region:      ${FAULT_REGION}"
echo "  Namespace:   ${NAMESPACE}"
echo "  Deployment:  ${DEPLOYMENT_NAME}"

if [[ "${FAULT_DRY_RUN}" == "true" ]]; then
    echo "[DRY-RUN] Would verify drift label, marker env var, and replica drift are present"
    exit 0
fi

LABELS=$(kubectl get deployment "${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    --show-labels \
    --no-headers | awk '{print $NF}')
MARKER=$(kubectl get deployment "${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    -o jsonpath='{.spec.template.spec.containers[0].env[?(@.name=="FAULT_INJECTION_DRIFT_MARKER")].value}' 2>/dev/null || true)
REPLICAS=$(kubectl get deployment "${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    -o jsonpath='{.spec.replicas}' 2>/dev/null || true)

if [[ "${LABELS}" == *"fault-injection-drift=true"* && -n "${MARKER}" && "${REPLICAS}" == "1" ]]; then
    echo "[FAULT-016] Verified manual drift is active"
    echo "  Labels:   ${LABELS}"
    echo "  Replicas: ${REPLICAS}"
    exit 0
fi

echo "[FAULT-016] ERROR: expected drift markers were not observed" >&2
echo "Labels: ${LABELS}" >&2
echo "Marker: ${MARKER}" >&2
echo "Replicas: ${REPLICAS}" >&2
exit 1
