#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

CUSTOMER_ROOT="$REPO_ROOT"
VALIDATION_WORKFLOW_NAME="Honua Operator Validation"
PREFLIGHT_WORKFLOW_NAME="Honua Operator Preflight"
HONUA_DEVOPS_REPOSITORY="honua-io/honua-devops"
HONUA_DEVOPS_REF="main"
OPERATOR_CHECKOUT_PATH=".tooling/honua-devops"
DOTNET_VERSION="10.0.x"
PROVIDER="codex"
GITOPS_TOOL="honua-gitops"
TERRAFORM_REPOSITORY="https://github.com/honua-io/honua-terraform"
TERRAFORM_REF="main"
TERRAFORM_TARGETS=""
HONUA_API_BASE_URL="http://localhost:8080"
OTEL_BASE_URL="http://localhost:4318"
CODEX_MODEL=""
CODEX_ENDPOINT=""
CLAUDE_MODEL=""
CLAUDE_ENDPOINT=""
SKIP_PREFLIGHT_WORKFLOW="false"
FORCE="false"

usage() {
  cat <<'EOF'
Usage:
  scripts/install-customer-ci.sh [options]

Options:
  --customer-root <path>                Default: current honua-devops repo root
  --validation-workflow-name <value>    Default: Honua Operator Validation
  --preflight-workflow-name <value>     Default: Honua Operator Preflight
  --honua-devops-repository <org/repo>  Default: honua-io/honua-devops
  --honua-devops-ref <ref>              Default: main
  --operator-checkout-path <path>       Default: .tooling/honua-devops
  --dotnet-version <value>              Default: 10.0.x
  --provider <codex|claude>             Default: codex
  --gitops-tool <name>                  Default: honua-gitops
  --terraform-repository <url>          Default: https://github.com/honua-io/honua-terraform
  --terraform-ref <ref>                 Default: main
  --terraform-targets <csv>             Default: empty; caller should provide initial target set
  --honua-api-base-url <url>            Default: http://localhost:8080
  --otel-base-url <url>                 Default: http://localhost:4318
  --codex-model <value>                 Optional starter repo variable
  --codex-endpoint <url>                Optional starter repo variable
  --claude-model <value>                Optional starter repo variable
  --claude-endpoint <url>               Optional starter repo variable
  --skip-preflight-workflow             Skip manual preflight workflow generation
  --force                               Overwrite generated workflow
  --help                                Show help
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

while [[ $# -gt 0 ]]; do
  case "$1" in
    --customer-root)
      require_value "$1" "${2:-}"
      CUSTOMER_ROOT="$2"
      shift 2
      ;;
    --validation-workflow-name)
      require_value "$1" "${2:-}"
      VALIDATION_WORKFLOW_NAME="$2"
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
    --operator-checkout-path)
      require_value "$1" "${2:-}"
      OPERATOR_CHECKOUT_PATH="$2"
      shift 2
      ;;
    --dotnet-version)
      require_value "$1" "${2:-}"
      DOTNET_VERSION="$2"
      shift 2
      ;;
    --provider)
      require_value "$1" "${2:-}"
      PROVIDER="$2"
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
    --terraform-targets)
      require_value "$1" "${2:-}"
      TERRAFORM_TARGETS="$2"
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
    --codex-model)
      require_value "$1" "${2:-}"
      CODEX_MODEL="$2"
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
    --claude-endpoint)
      require_value "$1" "${2:-}"
      CLAUDE_ENDPOINT="$2"
      shift 2
      ;;
    --skip-preflight-workflow)
      SKIP_PREFLIGHT_WORKFLOW="true"
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

case "$CUSTOMER_ROOT" in
  /*) ;;
  *) CUSTOMER_ROOT="$REPO_ROOT/$CUSTOMER_ROOT" ;;
esac

mkdir -p "$CUSTOMER_ROOT"
CUSTOMER_ROOT="$(cd "$CUSTOMER_ROOT" && pwd)"

VALIDATION_WORKFLOW_TOKEN="$(normalize_token "$VALIDATION_WORKFLOW_NAME" "honua-operator-validation")"
VALIDATION_WORKFLOW_PATH="$CUSTOMER_ROOT/.github/workflows/${VALIDATION_WORKFLOW_TOKEN}.yml"
PREFLIGHT_WORKFLOW_TOKEN="$(normalize_token "$PREFLIGHT_WORKFLOW_NAME" "honua-operator-preflight")"
PREFLIGHT_WORKFLOW_PATH="$CUSTOMER_ROOT/.github/workflows/${PREFLIGHT_WORKFLOW_TOKEN}.yml"
CONFIG_SCRIPT_PATH="$CUSTOMER_ROOT/bootstrap/configure-honua-operator-ci.sh"
mkdir -p "$(dirname "$VALIDATION_WORKFLOW_PATH")"
mkdir -p "$(dirname "$CONFIG_SCRIPT_PATH")"

if [[ -e "$VALIDATION_WORKFLOW_PATH" && "$FORCE" != "true" ]]; then
  echo "[ERROR] workflow file exists: $VALIDATION_WORKFLOW_PATH (use --force to overwrite)" >&2
  exit 2
fi

if [[ -e "$CONFIG_SCRIPT_PATH" && "$FORCE" != "true" ]]; then
  echo "[ERROR] config script exists: $CONFIG_SCRIPT_PATH (use --force to overwrite)" >&2
  exit 2
fi

if [[ "$SKIP_PREFLIGHT_WORKFLOW" != "true" && -e "$PREFLIGHT_WORKFLOW_PATH" && "$FORCE" != "true" ]]; then
  echo "[ERROR] workflow file exists: $PREFLIGHT_WORKFLOW_PATH (use --force to overwrite)" >&2
  exit 2
fi

cat > "$VALIDATION_WORKFLOW_PATH" <<EOF
name: $VALIDATION_WORKFLOW_NAME

on:
  pull_request:
  push:
    branches:
      - main
  workflow_dispatch:

permissions:
  contents: read

jobs:
  validate-desired-state:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout customer repo
        uses: actions/checkout@v4

      - name: Checkout honua-devops
        uses: actions/checkout@v4
        with:
          repository: $HONUA_DEVOPS_REPOSITORY
          ref: $HONUA_DEVOPS_REF
          path: $OPERATOR_CHECKOUT_PATH
          token: \${{ secrets.HONUA_DEVOPS_OPERATOR_GIT_TOKEN || github.token }}

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '$DOTNET_VERSION'

      - name: Validate desired state
        run: bash $OPERATOR_CHECKOUT_PATH/scripts/validate-desired-state.sh --root "\$GITHUB_WORKSPACE/desired-state"
EOF

if [[ "$SKIP_PREFLIGHT_WORKFLOW" != "true" ]]; then
  cat > "$PREFLIGHT_WORKFLOW_PATH" <<EOF
name: $PREFLIGHT_WORKFLOW_NAME

on:
  workflow_dispatch:

permissions:
  contents: read

jobs:
  preflight:
    runs-on: ubuntu-latest
    env:
      HONUA_DEVOPS_PROVIDER: \${{ vars.HONUA_DEVOPS_PROVIDER }}
      HONUA_DEVOPS_EXECUTION_MODE: \${{ vars.HONUA_DEVOPS_EXECUTION_MODE }}
      HONUA_DEVOPS_EXECUTION_TIER: \${{ vars.HONUA_DEVOPS_EXECUTION_TIER }}
      HONUA_DEVOPS_APPROVAL_MODE: \${{ vars.HONUA_DEVOPS_APPROVAL_MODE }}
      HONUA_DEVOPS_GITOPS_TOOL: \${{ vars.HONUA_DEVOPS_GITOPS_TOOL }}
      HONUA_DEVOPS_TERRAFORM_REPO: \${{ vars.HONUA_DEVOPS_TERRAFORM_REPO }}
      HONUA_DEVOPS_TERRAFORM_REF: \${{ vars.HONUA_DEVOPS_TERRAFORM_REF }}
      HONUA_DEVOPS_TERRAFORM_TARGETS: \${{ vars.HONUA_DEVOPS_TERRAFORM_TARGETS }}
      HONUA_DEVOPS_TERRAFORM_LOCAL_PATH: \${{ github.workspace }}/.tooling/honua-terraform
      HONUA_DEVOPS_HONUA_API_BASE_URL: \${{ vars.HONUA_DEVOPS_HONUA_API_BASE_URL }}
      HONUA_DEVOPS_OTEL_BASE_URL: \${{ vars.HONUA_DEVOPS_OTEL_BASE_URL }}
      HONUA_DEVOPS_HONUA_API_KEY: \${{ secrets.HONUA_DEVOPS_HONUA_API_KEY }}
      HONUA_DEVOPS_OTEL_API_KEY: \${{ secrets.HONUA_DEVOPS_OTEL_API_KEY }}
      HONUA_DEVOPS_CODEX_MODEL: \${{ vars.HONUA_DEVOPS_CODEX_MODEL }}
      HONUA_DEVOPS_CODEX_ENDPOINT: \${{ vars.HONUA_DEVOPS_CODEX_ENDPOINT }}
      HONUA_DEVOPS_CODEX_API_KEY: \${{ secrets.HONUA_DEVOPS_CODEX_API_KEY }}
      HONUA_DEVOPS_CLAUDE_MODEL: \${{ vars.HONUA_DEVOPS_CLAUDE_MODEL }}
      HONUA_DEVOPS_CLAUDE_ENDPOINT: \${{ vars.HONUA_DEVOPS_CLAUDE_ENDPOINT }}
      HONUA_DEVOPS_CLAUDE_API_KEY: \${{ secrets.HONUA_DEVOPS_CLAUDE_API_KEY }}
      HONUA_DEVOPS_TERRAFORM_GIT_TOKEN: \${{ secrets.HONUA_DEVOPS_TERRAFORM_GIT_TOKEN }}
    steps:
      - name: Checkout customer repo
        uses: actions/checkout@v4

      - name: Checkout honua-devops
        uses: actions/checkout@v4
        with:
          repository: $HONUA_DEVOPS_REPOSITORY
          ref: $HONUA_DEVOPS_REF
          path: $OPERATOR_CHECKOUT_PATH
          token: \${{ secrets.HONUA_DEVOPS_OPERATOR_GIT_TOKEN || github.token }}

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '$DOTNET_VERSION'

      - name: Clone honua-terraform
        shell: bash
        run: |
          set -euo pipefail
          repo="\${HONUA_DEVOPS_TERRAFORM_REPO:-$TERRAFORM_REPOSITORY}"
          ref="\${HONUA_DEVOPS_TERRAFORM_REF:-$TERRAFORM_REF}"
          clone_url="\$repo"
          if [[ -n "\${HONUA_DEVOPS_TERRAFORM_GIT_TOKEN:-}" && "\$repo" == https://github.com/* ]]; then
            clone_url="\${repo/https:\/\//https:\/\/x-access-token:\${HONUA_DEVOPS_TERRAFORM_GIT_TOKEN}@}"
          fi

          rm -rf "\$HONUA_DEVOPS_TERRAFORM_LOCAL_PATH"
          git clone --depth 1 --branch "\$ref" "\$clone_url" "\$HONUA_DEVOPS_TERRAFORM_LOCAL_PATH"

      - name: Run preflight
        run: dotnet run --project "$OPERATOR_CHECKOUT_PATH/src/Honua.DevOps.Agent" -- --preflight
EOF
fi

cat > "$CONFIG_SCRIPT_PATH" <<EOF
#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="\$(cd "\$(dirname "\${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="\$(cd "\$SCRIPT_DIR/.." && pwd)"
TARGET_REPOSITORY=""

usage() {
  cat <<'INNER_EOF'
Usage:
  bootstrap/configure-honua-operator-ci.sh [options]

Options:
  --repo <owner/repo>  Optional explicit GitHub repo for gh CLI operations
  --help               Show help
INNER_EOF
}

require_command() {
  local command_name="\$1"
  if ! command -v "\$command_name" >/dev/null 2>&1; then
    echo "[ERROR] required command not found: \$command_name" >&2
    exit 1
  fi
}

set_repo_args() {
  if [[ -n "\$TARGET_REPOSITORY" ]]; then
    printf '%s\n' "-R" "\$TARGET_REPOSITORY"
    return
  fi
}

set_variable() {
  local name="\$1"
  local value="\$2"
  gh variable set "\$name" \$(set_repo_args) --body "\$value"
  echo "Set repo variable: \$name"
}

set_secret_if_present() {
  local name="\$1"
  local value="\$2"
  if [[ -z "\$value" ]]; then
    echo "[WARN] skipped empty repo secret: \$name" >&2
    return
  fi

  gh secret set "\$name" \$(set_repo_args) --body "\$value"
  echo "Set repo secret: \$name"
}

while [[ \$# -gt 0 ]]; do
  case "\$1" in
    --repo)
      if [[ \$# -lt 2 || -z "\${2:-}" ]]; then
        echo "[ERROR] --repo requires a value" >&2
        exit 1
      fi
      TARGET_REPOSITORY="\$2"
      shift 2
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      echo "[ERROR] Unknown arg: \$1" >&2
      usage
      exit 1
      ;;
  esac
done

require_command gh

if [[ -f "\$REPO_ROOT/.env.local" ]]; then
  set -a
  # shellcheck disable=SC1091
  source "\$REPO_ROOT/.env.local"
  set +a
elif [[ -f "\$REPO_ROOT/.env" ]]; then
  set -a
  # shellcheck disable=SC1091
  source "\$REPO_ROOT/.env"
  set +a
fi

set_variable HONUA_DEVOPS_PROVIDER "\${HONUA_DEVOPS_PROVIDER:-$PROVIDER}"
set_variable HONUA_DEVOPS_EXECUTION_MODE "\${HONUA_DEVOPS_EXECUTION_MODE:-plan}"
set_variable HONUA_DEVOPS_EXECUTION_TIER "\${HONUA_DEVOPS_EXECUTION_TIER:-plan}"
set_variable HONUA_DEVOPS_APPROVAL_MODE "\${HONUA_DEVOPS_APPROVAL_MODE:-pr-first}"
set_variable HONUA_DEVOPS_GITOPS_TOOL "\${HONUA_DEVOPS_GITOPS_TOOL:-$GITOPS_TOOL}"
set_variable HONUA_DEVOPS_TERRAFORM_REPO "\${HONUA_DEVOPS_TERRAFORM_REPO:-$TERRAFORM_REPOSITORY}"
set_variable HONUA_DEVOPS_TERRAFORM_REF "\${HONUA_DEVOPS_TERRAFORM_REF:-$TERRAFORM_REF}"
set_variable HONUA_DEVOPS_TERRAFORM_TARGETS "\${HONUA_DEVOPS_TERRAFORM_TARGETS:-$TERRAFORM_TARGETS}"
set_variable HONUA_DEVOPS_HONUA_API_BASE_URL "\${HONUA_DEVOPS_HONUA_API_BASE_URL:-$HONUA_API_BASE_URL}"
set_variable HONUA_DEVOPS_OTEL_BASE_URL "\${HONUA_DEVOPS_OTEL_BASE_URL:-$OTEL_BASE_URL}"
set_variable HONUA_DEVOPS_CODEX_MODEL "\${HONUA_DEVOPS_CODEX_MODEL:-$CODEX_MODEL}"
set_variable HONUA_DEVOPS_CODEX_ENDPOINT "\${HONUA_DEVOPS_CODEX_ENDPOINT:-$CODEX_ENDPOINT}"
set_variable HONUA_DEVOPS_CLAUDE_MODEL "\${HONUA_DEVOPS_CLAUDE_MODEL:-$CLAUDE_MODEL}"
set_variable HONUA_DEVOPS_CLAUDE_ENDPOINT "\${HONUA_DEVOPS_CLAUDE_ENDPOINT:-$CLAUDE_ENDPOINT}"

set_secret_if_present HONUA_DEVOPS_HONUA_API_KEY "\${HONUA_DEVOPS_HONUA_API_KEY:-}"
set_secret_if_present HONUA_DEVOPS_OTEL_API_KEY "\${HONUA_DEVOPS_OTEL_API_KEY:-}"
set_secret_if_present HONUA_DEVOPS_CODEX_API_KEY "\${HONUA_DEVOPS_CODEX_API_KEY:-}"
set_secret_if_present HONUA_DEVOPS_CLAUDE_API_KEY "\${HONUA_DEVOPS_CLAUDE_API_KEY:-}"

echo "[WARN] configure HONUA_DEVOPS_OPERATOR_GIT_TOKEN if \`$HONUA_DEVOPS_REPOSITORY\` is private." >&2
echo "[WARN] configure HONUA_DEVOPS_TERRAFORM_GIT_TOKEN if \`$TERRAFORM_REPOSITORY\` requires authenticated clone in Actions." >&2
EOF

chmod +x "$CONFIG_SCRIPT_PATH"

echo "Installed customer CI workflow: $VALIDATION_WORKFLOW_PATH"
if [[ "$SKIP_PREFLIGHT_WORKFLOW" != "true" ]]; then
  echo "Installed customer CI workflow: $PREFLIGHT_WORKFLOW_PATH"
fi
echo "Installed customer CI helper: $CONFIG_SCRIPT_PATH"
