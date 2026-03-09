#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

CUSTOMER_ROOT="$REPO_ROOT"
SERVICE=""
RUNTIME_TARGET=""
PROVIDER="codex"
REVISION="release/2026.03"
ENVIRONMENTS="dev,staging,prod"
GITOPS_TOOL="honua-gitops"
TERRAFORM_REPOSITORY="https://github.com/honua-io/honua-terraform"
TERRAFORM_REF="main"
TERRAFORM_LOCAL_PATH=""
CI_WORKFLOW_NAME="Honua Operator Validation"
PREFLIGHT_WORKFLOW_NAME="Honua Operator Preflight"
HONUA_DEVOPS_REPOSITORY="honua-io/honua-devops"
HONUA_DEVOPS_REF="main"
HONUA_API_BASE_URL="http://localhost:8080"
OTEL_BASE_URL="http://localhost:4318"
HONUA_API_KEY=""
OTEL_API_KEY=""
CODEX_MODEL=""
CODEX_API_KEY=""
CODEX_ENDPOINT=""
CLAUDE_MODEL=""
CLAUDE_API_KEY=""
CLAUDE_ENDPOINT=""
RUN_PREFLIGHT="false"
SKIP_VALIDATE="false"
SKIP_CI="false"
SKIP_PREFLIGHT_CI="false"
FORCE="false"
SECRET_REFS=()

usage() {
  cat <<'EOF'
Usage:
  scripts/bootstrap-customer-repo.sh --service <name> --runtime-target <target> [options]

Options:
  --customer-root <path>              Default: current honua-devops repo root
  --service <name>                    Required. Service name to scaffold
  --runtime-target <target>           Required. One of the desired-state convention targets
  --provider <codex|claude>           Default: codex
  --revision <value>                  Default: release/2026.03
  --environments <csv>                Default: dev,staging,prod
  --gitops-tool <name>                Default: honua-gitops
  --terraform-repository <url>        Default: https://github.com/honua-io/honua-terraform
  --terraform-ref <ref>               Default: main
  --terraform-local-path <path>       Default: <customer-root>/../honua-terraform
  --ci-workflow-name <value>          Default: Honua Operator Validation
  --preflight-workflow-name <value>   Default: Honua Operator Preflight
  --honua-devops-repository <org/repo> Default: honua-io/honua-devops
  --honua-devops-ref <ref>            Default: main
  --honua-api-base-url <url>          Default: http://localhost:8080
  --otel-base-url <url>               Default: http://localhost:4318
  --honua-api-key <value>             Optional
  --otel-api-key <value>              Optional
  --codex-model <value>               Optional
  --codex-api-key <value>             Optional
  --codex-endpoint <url>              Optional
  --claude-model <value>              Optional
  --claude-api-key <value>            Optional
  --claude-endpoint <url>             Optional
  --secret-ref <name>                 Repeatable. Passed to desired-state scaffold
  --run-preflight                     Run preflight after writing .env.local
  --skip-validate                     Skip desired-state validation after scaffolding
  --skip-ci                           Skip customer CI workflow generation
  --skip-preflight-ci                 Skip preflight workflow generation inside customer CI assets
  --force                             Overwrite generated files
  --help                              Show help
EOF
}

require_value() {
  local flag="$1"
  local value="${2:-}"
  if [[ -z "$value" ]]; then
    echo "[ERROR] $flag requires a value" >&2
    exit 1
  fi
}

normalize_token() {
  local value="$1"
  local fallback="$2"
  local lowered normalized
  lowered="$(printf '%s' "$value" | tr '[:upper:]' '[:lower:]')"
  normalized="$(printf '%s' "$lowered" | sed -E 's/[^a-z0-9]+/-/g; s/^-+//; s/-+$//; s/-+/-/g')"
  if [[ -z "$normalized" ]]; then
    printf '%s' "$fallback"
    return
  fi

  printf '%s' "$normalized"
}

append_optional_arg() {
  local -n arg_list_ref="$1"
  local flag="$2"
  local value="$3"
  if [[ -n "$value" ]]; then
    arg_list_ref+=("$flag" "$value")
  fi
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --customer-root)
      require_value "$1" "${2:-}"
      CUSTOMER_ROOT="$2"
      shift 2
      ;;
    --service)
      require_value "$1" "${2:-}"
      SERVICE="$2"
      shift 2
      ;;
    --runtime-target)
      require_value "$1" "${2:-}"
      RUNTIME_TARGET="$2"
      shift 2
      ;;
    --provider)
      require_value "$1" "${2:-}"
      PROVIDER="$2"
      shift 2
      ;;
    --revision)
      require_value "$1" "${2:-}"
      REVISION="$2"
      shift 2
      ;;
    --environments)
      require_value "$1" "${2:-}"
      ENVIRONMENTS="$2"
      shift 2
      ;;
    --gitops-tool)
      require_value "$1" "${2:-}"
      GITOPS_TOOL="$2"
      shift 2
      ;;
    --terraform-repository)
      require_value "$1" "${2:-}"
      TERRAFORM_REPOSITORY="$2"
      shift 2
      ;;
    --terraform-ref)
      require_value "$1" "${2:-}"
      TERRAFORM_REF="$2"
      shift 2
      ;;
    --terraform-local-path)
      require_value "$1" "${2:-}"
      TERRAFORM_LOCAL_PATH="$2"
      shift 2
      ;;
    --ci-workflow-name)
      require_value "$1" "${2:-}"
      CI_WORKFLOW_NAME="$2"
      shift 2
      ;;
    --preflight-workflow-name)
      require_value "$1" "${2:-}"
      PREFLIGHT_WORKFLOW_NAME="$2"
      shift 2
      ;;
    --honua-devops-repository)
      require_value "$1" "${2:-}"
      HONUA_DEVOPS_REPOSITORY="$2"
      shift 2
      ;;
    --honua-devops-ref)
      require_value "$1" "${2:-}"
      HONUA_DEVOPS_REF="$2"
      shift 2
      ;;
    --honua-api-base-url)
      require_value "$1" "${2:-}"
      HONUA_API_BASE_URL="$2"
      shift 2
      ;;
    --otel-base-url)
      require_value "$1" "${2:-}"
      OTEL_BASE_URL="$2"
      shift 2
      ;;
    --honua-api-key)
      require_value "$1" "${2:-}"
      HONUA_API_KEY="$2"
      shift 2
      ;;
    --otel-api-key)
      require_value "$1" "${2:-}"
      OTEL_API_KEY="$2"
      shift 2
      ;;
    --codex-model)
      require_value "$1" "${2:-}"
      CODEX_MODEL="$2"
      shift 2
      ;;
    --codex-api-key)
      require_value "$1" "${2:-}"
      CODEX_API_KEY="$2"
      shift 2
      ;;
    --codex-endpoint)
      require_value "$1" "${2:-}"
      CODEX_ENDPOINT="$2"
      shift 2
      ;;
    --claude-model)
      require_value "$1" "${2:-}"
      CLAUDE_MODEL="$2"
      shift 2
      ;;
    --claude-api-key)
      require_value "$1" "${2:-}"
      CLAUDE_API_KEY="$2"
      shift 2
      ;;
    --claude-endpoint)
      require_value "$1" "${2:-}"
      CLAUDE_ENDPOINT="$2"
      shift 2
      ;;
    --secret-ref)
      require_value "$1" "${2:-}"
      SECRET_REFS+=("$2")
      shift 2
      ;;
    --run-preflight)
      RUN_PREFLIGHT="true"
      shift
      ;;
    --skip-validate)
      SKIP_VALIDATE="true"
      shift
      ;;
    --skip-ci)
      SKIP_CI="true"
      shift
      ;;
    --skip-preflight-ci)
      SKIP_PREFLIGHT_CI="true"
      shift
      ;;
    --force)
      FORCE="true"
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      echo "[ERROR] Unknown arg: $1" >&2
      usage
      exit 1
      ;;
  esac
done

if [[ -z "$SERVICE" || -z "$RUNTIME_TARGET" ]]; then
  usage
  exit 1
fi

case "$CUSTOMER_ROOT" in
  /*) ;;
  *) CUSTOMER_ROOT="$REPO_ROOT/$CUSTOMER_ROOT" ;;
esac

mkdir -p "$CUSTOMER_ROOT"
CUSTOMER_ROOT="$(cd "$CUSTOMER_ROOT" && pwd)"

if [[ -z "$TERRAFORM_LOCAL_PATH" ]]; then
  TERRAFORM_LOCAL_PATH="$CUSTOMER_ROOT/../honua-terraform"
fi

ENV_FILE="$CUSTOMER_ROOT/.env.local"
DESIRED_STATE_ROOT="$CUSTOMER_ROOT/desired-state"
SERVICE_TOKEN="$(normalize_token "$SERVICE" "service")"
CHECKLIST_PATH="$CUSTOMER_ROOT/bootstrap/${SERVICE_TOKEN}-bootstrap-checklist.md"
CI_WORKFLOW_TOKEN="$(normalize_token "$CI_WORKFLOW_NAME" "honua-operator-validation")"
CI_WORKFLOW_PATH="$CUSTOMER_ROOT/.github/workflows/${CI_WORKFLOW_TOKEN}.yml"
PREFLIGHT_WORKFLOW_TOKEN="$(normalize_token "$PREFLIGHT_WORKFLOW_NAME" "honua-operator-preflight")"
PREFLIGHT_WORKFLOW_PATH="$CUSTOMER_ROOT/.github/workflows/${PREFLIGHT_WORKFLOW_TOKEN}.yml"
CI_CONFIG_SCRIPT_PATH="$CUSTOMER_ROOT/bootstrap/configure-honua-operator-ci.sh"
mkdir -p "$(dirname "$CHECKLIST_PATH")"

bootstrap_args=(
  --provider "$PROVIDER"
  --output "$ENV_FILE"
  --terraform-local-path "$TERRAFORM_LOCAL_PATH"
  --honua-api-base-url "$HONUA_API_BASE_URL"
  --otel-base-url "$OTEL_BASE_URL"
)

append_optional_arg bootstrap_args --honua-api-key "$HONUA_API_KEY"
append_optional_arg bootstrap_args --otel-api-key "$OTEL_API_KEY"
append_optional_arg bootstrap_args --codex-model "$CODEX_MODEL"
append_optional_arg bootstrap_args --codex-api-key "$CODEX_API_KEY"
append_optional_arg bootstrap_args --codex-endpoint "$CODEX_ENDPOINT"
append_optional_arg bootstrap_args --claude-model "$CLAUDE_MODEL"
append_optional_arg bootstrap_args --claude-api-key "$CLAUDE_API_KEY"
append_optional_arg bootstrap_args --claude-endpoint "$CLAUDE_ENDPOINT"

if [[ "$RUN_PREFLIGHT" == "true" ]]; then
  bootstrap_args+=(--run-preflight)
fi

if [[ "$FORCE" == "true" ]]; then
  bootstrap_args+=(--force)
fi

bash "$REPO_ROOT/scripts/bootstrap-operator-env.sh" "${bootstrap_args[@]}"

scaffold_args=(
  --service "$SERVICE"
  --runtime-target "$RUNTIME_TARGET"
  --revision "$REVISION"
  --environments "$ENVIRONMENTS"
  --gitops-tool "$GITOPS_TOOL"
  --terraform-repository "$TERRAFORM_REPOSITORY"
  --terraform-ref "$TERRAFORM_REF"
  --output-root "$DESIRED_STATE_ROOT"
)

for secret_ref in "${SECRET_REFS[@]}"; do
  scaffold_args+=(--secret-ref "$secret_ref")
done

if [[ "$FORCE" == "true" ]]; then
  scaffold_args+=(--force)
fi

bash "$REPO_ROOT/scripts/scaffold-desired-state.sh" "${scaffold_args[@]}"

if [[ "$SKIP_VALIDATE" != "true" ]]; then
  bash "$REPO_ROOT/scripts/validate-desired-state.sh" --root "$DESIRED_STATE_ROOT"
fi

if [[ "$SKIP_CI" != "true" ]]; then
  ci_args=(
    --customer-root "$CUSTOMER_ROOT"
    --validation-workflow-name "$CI_WORKFLOW_NAME"
    --preflight-workflow-name "$PREFLIGHT_WORKFLOW_NAME"
    --honua-devops-repository "$HONUA_DEVOPS_REPOSITORY"
    --honua-devops-ref "$HONUA_DEVOPS_REF"
    --provider "$PROVIDER"
    --gitops-tool "$GITOPS_TOOL"
    --terraform-repository "$TERRAFORM_REPOSITORY"
    --terraform-ref "$TERRAFORM_REF"
    --terraform-targets "$RUNTIME_TARGET"
    --honua-api-base-url "$HONUA_API_BASE_URL"
    --otel-base-url "$OTEL_BASE_URL"
  )

  append_optional_arg ci_args --codex-model "$CODEX_MODEL"
  append_optional_arg ci_args --codex-endpoint "$CODEX_ENDPOINT"
  append_optional_arg ci_args --claude-model "$CLAUDE_MODEL"
  append_optional_arg ci_args --claude-endpoint "$CLAUDE_ENDPOINT"

  if [[ "$SKIP_PREFLIGHT_CI" == "true" ]]; then
    ci_args+=(--skip-preflight-workflow)
  fi

  if [[ "$FORCE" == "true" ]]; then
    ci_args+=(--force)
  fi

  bash "$REPO_ROOT/scripts/install-customer-ci.sh" "${ci_args[@]}"
fi

cat > "$CHECKLIST_PATH" <<EOF
# Customer Bootstrap Checklist

Generated by \`scripts/bootstrap-customer-repo.sh\`.

## Inputs

- Customer root: \`$CUSTOMER_ROOT\`
- Operator source: \`$REPO_ROOT\`
- Service: \`$SERVICE\`
- Runtime target: \`$RUNTIME_TARGET\`
- Provider: \`$PROVIDER\`
- Environments: \`$ENVIRONMENTS\`
- Revision: \`$REVISION\`

## Generated Artifacts

- Env overrides: \`$ENV_FILE\`
- Desired-state root: \`$DESIRED_STATE_ROOT\`
- Service releases: \`$DESIRED_STATE_ROOT/releases/$SERVICE_TOKEN/\`
- Service bundles: \`$DESIRED_STATE_ROOT/bundles/$SERVICE_TOKEN/\`
- Promotions: \`$DESIRED_STATE_ROOT/promotions/$SERVICE_TOKEN/\`
EOF

if [[ "$SKIP_CI" != "true" ]]; then
  cat >> "$CHECKLIST_PATH" <<EOF
- Customer CI workflow: \`$CI_WORKFLOW_PATH\`
- Customer CI helper: \`$CI_CONFIG_SCRIPT_PATH\`
EOF
fi

if [[ "$SKIP_CI" != "true" && "$SKIP_PREFLIGHT_CI" != "true" ]]; then
  cat >> "$CHECKLIST_PATH" <<EOF
- Customer preflight workflow: \`$PREFLIGHT_WORKFLOW_PATH\`
EOF
fi

cat >> "$CHECKLIST_PATH" <<EOF

## Checklist

1. Fill in provider credentials in \`$ENV_FILE\`.
2. Confirm backend URLs and Terraform local path are correct.
3. Review the scaffolded desired-state files for the first service.
4. Run desired-state validation after edits:
   \`$REPO_ROOT/scripts/validate-desired-state.sh --root "$DESIRED_STATE_ROOT"\`
5. Run preflight from the customer root:
   \`(cd "$CUSTOMER_ROOT" && dotnet run --project "$REPO_ROOT/src/Honua.DevOps.Agent" -- --preflight)\`
6. Load GitHub repo vars/secrets for generated Actions workflows:
   \`(cd "$CUSTOMER_ROOT" && ./bootstrap/configure-honua-operator-ci.sh)\`
7. Start the first rollout in plan mode:
   \`(cd "$CUSTOMER_ROOT" && dotnet run --project "$REPO_ROOT/src/Honua.DevOps.Agent" -- --provider "$PROVIDER" --prompt "Use deploy_service_gitops to plan rollout of $SERVICE revision $REVISION to $ENVIRONMENTS with change summary 'customer bootstrap plan'.")\`
EOF

if [[ "$SKIP_CI" != "true" ]]; then
  cat >> "$CHECKLIST_PATH" <<EOF
8. Review the generated GitHub Actions workflows and adjust the \`honua-devops\` repository/ref if you mirror or vendor the operator internally.
EOF
fi

echo "Customer bootstrap complete."
echo "Customer root: $CUSTOMER_ROOT"
echo "Checklist: $CHECKLIST_PATH"
