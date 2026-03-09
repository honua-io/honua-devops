#!/usr/bin/env bash

set -euo pipefail

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

ENV_FILE="$WORKDIR/rotation.env"
ROTATION_DIR="$WORKDIR/rotation"
REVOCATION_DIR="$WORKDIR/revocation"

cat >"$ENV_FILE" <<'EOF'
HONUA_DEVOPS_HONUA_API_KEY=fake-honua-key
HONUA_DEVOPS_OTEL_API_KEY=fake-otel-key
HONUA_DEVOPS_TERRAFORM_GIT_TOKEN=fake-terraform-token
EOF

./scripts/rotate-operator-secrets.sh \
  --env-file "$ENV_FILE" \
  --output-dir "$ROTATION_DIR" \
  --dry-run \
  --reason "smoke-rotation" \
  --cadence "30d"

./scripts/revoke-operator-secrets.sh \
  --secret HONUA_DEVOPS_HONUA_API_KEY \
  --secret HONUA_DEVOPS_OTEL_API_KEY \
  --output-dir "$REVOCATION_DIR" \
  --dry-run \
  --reason "smoke-revocation"

test -f "$ROTATION_DIR/rotation-evidence.json"
test -f "$REVOCATION_DIR/revocation-evidence.json"
python3 -m json.tool "$ROTATION_DIR/rotation-evidence.json" >/dev/null
python3 -m json.tool "$REVOCATION_DIR/revocation-evidence.json" >/dev/null
rg -n 'HONUA_DEVOPS_HONUA_API_KEY' "$ROTATION_DIR/rotation-evidence.json" >/dev/null
rg -n 'HONUA_DEVOPS_OTEL_API_KEY' "$REVOCATION_DIR/revocation-evidence.json" >/dev/null

echo "Secret lifecycle smoke check passed."
