#!/usr/bin/env bash
# FAULT-016-inject.sh — Manually apply config outside GitOps flow
# Scenario: Drifted GitOps revision or manual config drift
# Supports: EKS / AKS / ECS / ACA (any GitOps-managed resource)
set -euo pipefail

# --- Guard: required environment variables ---
: "${FAULT_ENV:?FAULT_ENV is required (e.g., dev, staging)}"
: "${FAULT_REGION:?FAULT_REGION is required (e.g., us-west-2, eastus)}"
: "${FAULT_RESOURCE_PREFIX:?FAULT_RESOURCE_PREFIX is required}"
: "${FAULT_DRY_RUN:=false}"

NAMESPACE="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}"
DEPLOYMENT_NAME="${FAULT_RESOURCE_PREFIX}-api"
DRIFT_LABEL="fault-injection-drift"
DRIFT_ANNOTATION="fault-injection/drifted-at=$(date -u +%Y-%m-%dT%H:%M:%SZ)"

echo "[FAULT-016] Injecting manual config drift outside GitOps"
echo "  Environment: ${FAULT_ENV}"
echo "  Region:      ${FAULT_REGION}"
echo "  Namespace:   ${NAMESPACE}"
echo "  Deployment:  ${DEPLOYMENT_NAME}"

if [[ "${FAULT_DRY_RUN}" == "true" ]]; then
    echo "[DRY-RUN] Would manually modify deployment '${DEPLOYMENT_NAME}' outside GitOps"
    echo "[DRY-RUN] Changes: add drift label, set replica count to 1, add extra env var"
    echo "[DRY-RUN] No changes made"
    exit 0
fi

echo "[K8s] Saving current state for verification..."
CURRENT_REPLICAS=$(kubectl get deployment "${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    -o jsonpath='{.spec.replicas}' 2>/dev/null || echo "3")

echo "[K8s] Current replica count: ${CURRENT_REPLICAS}"

# Store original state in a ConfigMap
kubectl create configmap "fault-injection-${DEPLOYMENT_NAME}-drift-state" \
    --from-literal=original-replicas="${CURRENT_REPLICAS}" \
    -n "${NAMESPACE}" \
    --dry-run=client -o yaml | kubectl apply -f -

# Apply manual changes that create GitOps drift
echo "[K8s] Applying manual changes outside GitOps..."

# 1. Add a drift label
kubectl label deployment "${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    "${DRIFT_LABEL}=true" \
    --overwrite

# 2. Add a drift annotation
kubectl annotate deployment "${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    "${DRIFT_ANNOTATION}" \
    --overwrite

# 3. Scale to a different replica count (manual drift)
kubectl scale deployment "${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    --replicas=1

# 4. Add an extra environment variable not in the GitOps manifest
kubectl set env deployment/"${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    FAULT_INJECTION_DRIFT_MARKER="This was manually applied outside GitOps"

echo "[K8s] Manual drift applied:"
echo "  - Added label: ${DRIFT_LABEL}=true"
echo "  - Added annotation: ${DRIFT_ANNOTATION}"
echo "  - Scaled replicas from ${CURRENT_REPLICAS} to 1"
echo "  - Added env var FAULT_INJECTION_DRIFT_MARKER"

echo "[FAULT-016] Injection complete — GitOps sync should detect drift"
