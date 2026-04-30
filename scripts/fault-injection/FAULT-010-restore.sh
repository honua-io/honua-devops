#!/usr/bin/env bash
# FAULT-010-restore.sh — Roll back to previous deployment revision
# Scenario: Rollout to bad image tag causing CrashLoop
# Supports: EKS / AKS (Kubernetes)
set -euo pipefail

# --- Guard: required environment variables ---
: "${FAULT_ENV:?FAULT_ENV is required (e.g., dev, staging)}"
: "${FAULT_REGION:?FAULT_REGION is required (e.g., us-west-2, eastus)}"
: "${FAULT_RESOURCE_PREFIX:?FAULT_RESOURCE_PREFIX is required}"
: "${FAULT_DRY_RUN:=false}"

NAMESPACE="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}"
DEPLOYMENT_NAME="${FAULT_RESOURCE_PREFIX}-api"

echo "[FAULT-010] Restoring deployment to previous revision"
echo "  Environment: ${FAULT_ENV}"
echo "  Region:      ${FAULT_REGION}"
echo "  Namespace:   ${NAMESPACE}"
echo "  Deployment:  ${DEPLOYMENT_NAME}"

if [[ "${FAULT_DRY_RUN}" == "true" ]]; then
    echo "[DRY-RUN] Would roll back deployment '${DEPLOYMENT_NAME}' to previous revision"
    echo "[DRY-RUN] No changes made"
    exit 0
fi

# Try to restore from the saved ConfigMap first
echo "[K8s] Attempting to restore from saved rollback image..."
PREVIOUS_IMAGE=$(kubectl get configmap "fault-injection-${DEPLOYMENT_NAME}-rollback" \
    -n "${NAMESPACE}" \
    -o jsonpath='{.data.previous-image}' 2>/dev/null || echo "")

if [[ -n "${PREVIOUS_IMAGE}" ]]; then
    echo "[K8s] Restoring image to: ${PREVIOUS_IMAGE}"
    kubectl set image deployment/"${DEPLOYMENT_NAME}" \
        "${DEPLOYMENT_NAME}=${PREVIOUS_IMAGE}" \
        -n "${NAMESPACE}"
else
    echo "[K8s] No saved image found, using kubectl rollout undo..."
    kubectl rollout undo deployment/"${DEPLOYMENT_NAME}" \
        -n "${NAMESPACE}"
fi

echo "[K8s] Waiting for rollout to complete..."
kubectl rollout status deployment/"${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    --timeout=120s

# Clean up the rollback ConfigMap
kubectl delete configmap "fault-injection-${DEPLOYMENT_NAME}-rollback" \
    -n "${NAMESPACE}" 2>/dev/null || true

echo "[K8s] Verifying pods are running..."
kubectl get pods -n "${NAMESPACE}" -l "app=${DEPLOYMENT_NAME}" --no-headers | head -5

echo "[FAULT-010] Restoration complete"
