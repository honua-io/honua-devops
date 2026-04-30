#!/usr/bin/env bash
# FAULT-016-verify-restored.sh - Verify manual GitOps drift is removed
set -euo pipefail

: "${FAULT_ENV:?FAULT_ENV is required (e.g., dev, staging)}"
: "${FAULT_REGION:?FAULT_REGION is required (e.g., us-west-2, eastus)}"
: "${FAULT_RESOURCE_PREFIX:?FAULT_RESOURCE_PREFIX is required}"
: "${FAULT_DRY_RUN:=false}"

NAMESPACE="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}"
DEPLOYMENT_NAME="${FAULT_RESOURCE_PREFIX}-api"

echo "[FAULT-016] Verifying manual GitOps drift is restored"
echo "  Environment: ${FAULT_ENV}"
echo "  Region:      ${FAULT_REGION}"
echo "  Namespace:   ${NAMESPACE}"
echo "  Deployment:  ${DEPLOYMENT_NAME}"

if [[ "${FAULT_DRY_RUN}" == "true" ]]; then
    echo "[DRY-RUN] Would verify drift markers are absent and deployment rollout is healthy"
    exit 0
fi

kubectl rollout status deployment/"${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    --timeout=60s

LABELS=$(kubectl get deployment "${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    --show-labels \
    --no-headers | awk '{print $NF}')
MARKER=$(kubectl get deployment "${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    -o jsonpath='{.spec.template.spec.containers[0].env[?(@.name=="FAULT_INJECTION_DRIFT_MARKER")].value}' 2>/dev/null || true)

if [[ "${LABELS}" == *"fault-injection-drift=true"* || -n "${MARKER}" ]]; then
    echo "[FAULT-016] ERROR: drift markers still present" >&2
    echo "Labels: ${LABELS}" >&2
    echo "Marker: ${MARKER}" >&2
    exit 1
fi

echo "[FAULT-016] Verified manual drift markers are removed"
