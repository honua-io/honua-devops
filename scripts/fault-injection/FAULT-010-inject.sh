#!/usr/bin/env bash
# FAULT-010-inject.sh — Deploy a nonexistent image tag causing CrashLoopBackOff
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
BAD_IMAGE_TAG="nonexistent-fault-injection-$(date +%s)"

echo "[FAULT-010] Injecting bad image tag deployment"
echo "  Environment: ${FAULT_ENV}"
echo "  Region:      ${FAULT_REGION}"
echo "  Namespace:   ${NAMESPACE}"
echo "  Deployment:  ${DEPLOYMENT_NAME}"
echo "  Bad tag:     ${BAD_IMAGE_TAG}"

if [[ "${FAULT_DRY_RUN}" == "true" ]]; then
    echo "[DRY-RUN] Would set image tag to '${BAD_IMAGE_TAG}' on deployment '${DEPLOYMENT_NAME}'"
    echo "[DRY-RUN] No changes made"
    exit 0
fi

# Save current image for rollback
echo "[K8s] Saving current image for rollback..."
CURRENT_IMAGE=$(kubectl get deployment "${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    -o jsonpath='{.spec.template.spec.containers[0].image}' 2>/dev/null)

if [[ -z "${CURRENT_IMAGE}" ]]; then
    echo "[K8s] ERROR: Could not retrieve current image for deployment '${DEPLOYMENT_NAME}'"
    exit 1
fi

echo "[K8s] Current image: ${CURRENT_IMAGE}"

# Store current image in a ConfigMap for rollback
kubectl create configmap "fault-injection-${DEPLOYMENT_NAME}-rollback" \
    --from-literal=previous-image="${CURRENT_IMAGE}" \
    -n "${NAMESPACE}" \
    --dry-run=client -o yaml | kubectl apply -f -

# Extract repository from current image
IMAGE_REPO="${CURRENT_IMAGE%%:*}"
BAD_IMAGE="${IMAGE_REPO}:${BAD_IMAGE_TAG}"

echo "[K8s] Setting image to: ${BAD_IMAGE}"
kubectl set image deployment/"${DEPLOYMENT_NAME}" \
    "${DEPLOYMENT_NAME}=${BAD_IMAGE}" \
    -n "${NAMESPACE}" \
    --record 2>/dev/null || \
kubectl set image deployment/"${DEPLOYMENT_NAME}" \
    "${DEPLOYMENT_NAME}=${BAD_IMAGE}" \
    -n "${NAMESPACE}"

echo "[K8s] Waiting for rollout to show failure..."
sleep 5

# Check rollout status (expected to fail)
kubectl rollout status deployment/"${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    --timeout=30s 2>&1 || echo "[K8s] Expected: rollout did not complete (bad image)"

echo "[FAULT-010] Injection complete — deployment should be in CrashLoopBackOff or ImagePullBackOff"
