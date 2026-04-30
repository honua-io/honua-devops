#!/usr/bin/env bash
# FAULT-010-verify-injected.sh - Verify bad image rollout is active
set -euo pipefail

: "${FAULT_ENV:?FAULT_ENV is required (e.g., dev, staging)}"
: "${FAULT_REGION:?FAULT_REGION is required (e.g., us-west-2, eastus)}"
: "${FAULT_RESOURCE_PREFIX:?FAULT_RESOURCE_PREFIX is required}"
: "${FAULT_DRY_RUN:=false}"

NAMESPACE="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}"
DEPLOYMENT_NAME="${FAULT_RESOURCE_PREFIX}-api"

echo "[FAULT-010] Verifying bad image rollout"
echo "  Environment: ${FAULT_ENV}"
echo "  Region:      ${FAULT_REGION}"
echo "  Namespace:   ${NAMESPACE}"
echo "  Deployment:  ${DEPLOYMENT_NAME}"

if [[ "${FAULT_DRY_RUN}" == "true" ]]; then
    echo "[DRY-RUN] Would verify deployment image contains nonexistent-fault-injection or pods are failing image pull"
    exit 0
fi

CURRENT_IMAGE=$(kubectl get deployment "${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    -o jsonpath='{.spec.template.spec.containers[0].image}' 2>/dev/null || true)
POD_REASONS=$(kubectl get pods -n "${NAMESPACE}" \
    -o jsonpath='{range .items[*]}{.metadata.name}{" "}{range .status.containerStatuses[*]}{.state.waiting.reason}{" "}{end}{"\n"}{end}' 2>/dev/null || true)

if [[ "${CURRENT_IMAGE}" == *"nonexistent-fault-injection"* ]]; then
    echo "[FAULT-010] Verified bad deployment image: ${CURRENT_IMAGE}"
    exit 0
fi

if echo "${POD_REASONS}" | grep -E "${DEPLOYMENT_NAME}.*(ImagePullBackOff|ErrImagePull|CrashLoopBackOff)" >/dev/null; then
    echo "[FAULT-010] Verified failing pod status"
    echo "${POD_REASONS}"
    exit 0
fi

echo "[FAULT-010] ERROR: bad image rollout not observed" >&2
echo "Current image: ${CURRENT_IMAGE}" >&2
echo "Pod reasons: ${POD_REASONS}" >&2
exit 1
