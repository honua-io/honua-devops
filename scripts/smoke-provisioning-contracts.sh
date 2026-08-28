#!/usr/bin/env bash
# Offline guard for the published provisioning contracts (honua-devops#147).
#
# honua-devops validates every handoff, verification receipt and provision binding
# against the files in `contracts/` ON WRITE, using the embedded copy of the same
# file a downstream consumer reads. That only holds if:
#
#   1. every provisioning contract parses as JSON, and
#   2. every one of them is embedded in the agent assembly.
#
# A schema added to `contracts/` but never embedded would be a contract nothing
# enforces -- exactly the state this ticket set out to fix -- so that is an error
# here rather than a discovery later.
#
# Needs no .NET, no network and no credentials; the deeper schema semantics are
# covered by ProvisioningContractSchemaTests in the .NET suite.
#
# Usage: ./scripts/smoke-provisioning-contracts.sh

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSPROJ="$REPO_ROOT/src/Honua.DevOps.Agent/Honua.DevOps.Agent.csproj"

command -v python3 >/dev/null 2>&1 || {
  echo "[ERROR] python3 is required" >&2
  exit 1
}

# The contracts honua-devops enforces at runtime.
PROVISIONING_CONTRACTS=(
  "honua-devops-aws-ecs-provision-binding.schema.json"
  "honua-mcp-proxy-handoff.v1.schema.json"
  "honua-devops-install-handoff-verification.v1.schema.json"
  "honua-devops-provision-approval.v1.schema.json"
)

FAILURES=0

fail() {
  echo "[FAIL] $*" >&2
  FAILURES=$((FAILURES + 1))
}

pass() {
  echo "[PASS] $*"
}

# 1. Every JSON file under contracts/ must parse.
while IFS= read -r schema; do
  if python3 -c 'import json,sys; json.load(open(sys.argv[1]))' "$schema" 2>/dev/null; then
    pass "parses: ${schema#"$REPO_ROOT/"}"
  else
    fail "does not parse: ${schema#"$REPO_ROOT/"}"
  fi
done < <(find "$REPO_ROOT/contracts" -maxdepth 1 -name '*.json' | sort)

# 2. Every provisioning contract must be embedded, and must declare an $id.
for contract in "${PROVISIONING_CONTRACTS[@]}"; do
  path="$REPO_ROOT/contracts/$contract"

  if [[ ! -f "$path" ]]; then
    fail "missing contract: contracts/$contract"
    continue
  fi

  if grep -qF "contracts/$contract" "$CSPROJ"; then
    pass "embedded: contracts/$contract"
  else
    fail "contracts/$contract is not an EmbeddedResource in $(basename "$CSPROJ"); it would be validated against nothing"
  fi

  if python3 -c 'import json,sys; sys.exit(0 if "$id" in json.load(open(sys.argv[1])) else 1)' "$path" 2>/dev/null; then
    pass "declares \$id: contracts/$contract"
  else
    fail "contracts/$contract declares no \$id"
  fi
done

# 3. Every honua-iac test fixture must be tracked by git.
#
# The repo-wide `.gitignore` excludes `artifacts/`, which once silently swallowed
# the captured substrate documents: the suite passed locally, where the files
# existed on disk, and failed in CI, which never received them. An untracked
# fixture is therefore an error here rather than a red build later.
FIXTURE_DIR="$REPO_ROOT/tests/Honua.DevOps.Agent.Tests/fixtures/honua-iac"
if [[ -d "$FIXTURE_DIR" ]]; then
  while IFS= read -r fixture; do
    relative="${fixture#"$REPO_ROOT/"}"
    if git -C "$REPO_ROOT" ls-files --error-unmatch "$relative" >/dev/null 2>&1; then
      pass "tracked: $relative"
    else
      fail "$relative is not tracked by git (check .gitignore); CI would not receive it"
    fi
  done < <(find "$FIXTURE_DIR" -type f | sort)
fi

if [[ "$FAILURES" -ne 0 ]]; then
  echo "[ERROR] provisioning contract smoke: $FAILURES failure(s)" >&2
  exit 1
fi

echo "[INFO] provisioning contract smoke: all checks passed"
