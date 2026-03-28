#!/usr/bin/env bash

set -euo pipefail

HELM_ENFORCE_PROVENANCE="${HELM_ENFORCE_PROVENANCE:-false}"
HELM_REPO_URL="${HELM_REPO_URL:-https://github.com/honua-io/honua-helm.git}"
HELM_CHART_PATH="${HELM_CHART_PATH:-}"
HELM_SIGNED_PACKAGE_URL="${HELM_SIGNED_PACKAGE_URL:-}"
HELM_PROV_URL="${HELM_PROV_URL:-}"
HELM_KEYRING_PATH="${HELM_KEYRING_PATH:-}"

require_cmd() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "[ERROR] Missing command: $1" >&2
    exit 1
  fi
}

require_cmd git
require_cmd helm

workdir="$(mktemp -d)"
trap 'rm -rf "$workdir"' EXIT

echo "[INFO] Cloning $HELM_REPO_URL"
git clone --depth 1 "$HELM_REPO_URL" "$workdir/honua-helm" >/dev/null

if [[ -n "$HELM_CHART_PATH" ]]; then
  chart_dir="$workdir/honua-helm/$HELM_CHART_PATH"
else
  chart_dir=""
  candidate_chart_dir=""
  for candidate_chart_dir in \
    "$workdir/honua-helm/charts/honua" \
    "$workdir/honua-helm/honua"; do
    if [[ -d "$candidate_chart_dir" ]]; then
      chart_dir="$candidate_chart_dir"
      break
    fi
  done
fi

if [[ ! -d "$chart_dir" ]]; then
  echo "[ERROR] Chart path not found. Set HELM_CHART_PATH to the chart directory inside honua-helm." >&2
  exit 1
fi

echo "[INFO] Running helm lint"
helm lint "$chart_dir"

echo "[INFO] Ensuring Helm dependency repositories are configured"
helm repo add bitnami https://charts.bitnami.com/bitnami --force-update >/dev/null
helm repo update >/dev/null

echo "[INFO] Building chart dependencies"
helm dependency build "$chart_dir" >/dev/null

echo "[INFO] Packaging chart"
mkdir -p "$workdir/dist"
helm package "$chart_dir" --destination "$workdir/dist" >/dev/null

if [[ "$HELM_ENFORCE_PROVENANCE" != "true" ]]; then
  echo "[WARN] HELM_ENFORCE_PROVENANCE=false; static checks complete, strict provenance skipped."
  exit 0
fi

if [[ -z "$HELM_SIGNED_PACKAGE_URL" || -z "$HELM_PROV_URL" || -z "$HELM_KEYRING_PATH" ]]; then
  echo "[ERROR] Strict provenance requires HELM_SIGNED_PACKAGE_URL, HELM_PROV_URL, HELM_KEYRING_PATH." >&2
  exit 2
fi

pkg="$workdir/dist/signed-chart.tgz"
prov="$workdir/dist/signed-chart.tgz.prov"

echo "[INFO] Downloading signed package and provenance files"
curl --silent --show-error --fail -L "$HELM_SIGNED_PACKAGE_URL" -o "$pkg"
curl --silent --show-error --fail -L "$HELM_PROV_URL" -o "$prov"

echo "[INFO] Verifying provenance signature"
helm verify "$pkg" --keyring "$HELM_KEYRING_PATH"
echo "[RESULT] Helm provenance verification passed."
