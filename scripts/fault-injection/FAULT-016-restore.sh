#!/usr/bin/env bash
# FAULT-016-restore.sh — Run honua-gitops sync to restore drifted config
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

echo "[FAULT-016] Restoring GitOps-managed config (removing manual drift)"
echo "  Environment: ${FAULT_ENV}"
echo "  Region:      ${FAULT_REGION}"
echo "  Namespace:   ${NAMESPACE}"
echo "  Deployment:  ${DEPLOYMENT_NAME}"

if [[ "${FAULT_DRY_RUN}" == "true" ]]; then
    echo "[DRY-RUN] Would trigger GitOps sync to restore '${DEPLOYMENT_NAME}' to desired state"
    echo "[DRY-RUN] No changes made"
    exit 0
fi

# Step 1: Remove the manually applied drift markers
echo "[K8s] Removing manual drift markers..."

# Remove drift label
kubectl label deployment "${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    "fault-injection-drift-" 2>/dev/null || true

# Remove drift annotation
kubectl annotate deployment "${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    "fault-injection/drifted-at-" 2>/dev/null || true

# Remove the injected environment variable
kubectl set env deployment/"${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    FAULT_INJECTION_DRIFT_MARKER- 2>/dev/null || true

# Step 2: Restore original replica count
ORIGINAL_REPLICAS=$(kubectl get configmap "fault-injection-${DEPLOYMENT_NAME}-drift-state" \
    -n "${NAMESPACE}" \
    -o jsonpath='{.data.original-replicas}' 2>/dev/null || echo "3")

echo "[K8s] Restoring replica count to: ${ORIGINAL_REPLICAS}"
kubectl scale deployment "${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    --replicas="${ORIGINAL_REPLICAS}"

# Step 3: Trigger GitOps sync if available
echo "[GitOps] Triggering sync to restore desired state..."

# Try ArgoCD sync first
if command -v argocd &>/dev/null; then
    ARGOCD_APP="${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}"
    echo "[ArgoCD] Syncing application: ${ARGOCD_APP}"
    argocd app sync "${ARGOCD_APP}" --force 2>/dev/null || \
        echo "[ArgoCD] Sync command failed — may need manual ArgoCD intervention"
# Try Flux reconciliation
elif command -v flux &>/dev/null; then
    echo "[Flux] Reconciling kustomization..."
    flux reconcile kustomization "${FAULT_RESOURCE_PREFIX}-${FAULT_ENV}" \
        --with-source 2>/dev/null || \
        echo "[Flux] Reconcile command failed — may need manual Flux intervention"
else
    echo "[GitOps] No ArgoCD or Flux CLI found — manual sync may be required"
    echo "[GitOps] Drift markers have been removed and replicas restored"
fi

# Step 4: Wait for rollout
echo "[K8s] Waiting for deployment to stabilize..."
kubectl rollout status deployment/"${DEPLOYMENT_NAME}" \
    -n "${NAMESPACE}" \
    --timeout=120s 2>/dev/null || echo "[K8s] Warning: rollout did not complete within timeout"

# Clean up
kubectl delete configmap "fault-injection-${DEPLOYMENT_NAME}-drift-state" \
    -n "${NAMESPACE}" 2>/dev/null || true

echo "[FAULT-016] Restoration complete"
