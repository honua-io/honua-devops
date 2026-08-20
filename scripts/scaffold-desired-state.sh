#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
CONVENTIONS_FILE="$REPO_ROOT/desired-state/conventions.env"

# shellcheck disable=SC1090
source "$CONVENTIONS_FILE"

SERVICE=""
RUNTIME_TARGET=""
REVISION="release/2026.03"
GITOPS_TOOL="honua-gitops"
TERRAFORM_REPOSITORY="https://github.com/honua-io/honua-iac"
TERRAFORM_REF="trunk"
ENVIRONMENTS_CSV="dev,staging,prod"
OUTPUT_ROOT="$REPO_ROOT/desired-state"
FORCE="false"
SECRET_REFS=()

usage() {
  cat <<'EOF'
Usage:
  scripts/scaffold-desired-state.sh --service <name> --runtime-target <target> [options]

Options:
  --service <name>                    Required. Service name, e.g. roads-api
  --runtime-target <target>           Required. Allowed values come from desired-state/conventions.env
  --revision <value>                  Default: release/2026.03
  --gitops-tool <name>                Default: honua-gitops
  --terraform-repository <url>        Default: https://github.com/honua-io/honua-iac
  --terraform-ref <ref>               Default: main
  --environments <csv>                Default: dev,staging,prod
  --output-root <path>                Default: desired-state under the repo root
  --secret-ref <name>                 Repeatable. Default: HONUA_ADMIN_API_KEY, HONUA_DB_CONNECTION
  --force                             Overwrite existing files
  --help                              Show help
EOF
}

trim() {
  local value="$1"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  printf '%s' "$value"
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

render_name_template() {
  local template="$1"
  shift

  local assignment key value
  for assignment in "$@"; do
    key="${assignment%%=*}"
    value="${assignment#*=}"
    template="${template//\{$key\}/$value}"
  done

  printf '%s' "$template"
}

validate_service_name() {
  local value="$1"
  if [[ ! "$value" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$ ]]; then
    echo "[ERROR] service must match [A-Za-z0-9][A-Za-z0-9._-]{0,79}" >&2
    exit 1
  fi
}

validate_runtime_target() {
  local value="$1"
  local allowed_targets_csv=",${ALLOWED_RUNTIME_TARGETS},"
  if [[ "$allowed_targets_csv" != *",$value,"* ]]; then
    echo "[ERROR] runtime target must be one of: ${ALLOWED_RUNTIME_TARGETS}" >&2
    exit 1
  fi
}

validate_environment() {
  local value="$1"
  if [[ ! "$value" =~ ^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$ ]]; then
    echo "[ERROR] invalid environment token: $value" >&2
    exit 1
  fi
}

write_file() {
  local path="$1"
  local body="$2"
  local directory
  directory="$(dirname "$path")"
  mkdir -p "$directory"

  if [[ -e "$path" && "$FORCE" != "true" ]]; then
    echo "[ERROR] file exists: $path (use --force to overwrite)" >&2
    exit 2
  fi

  printf '%s\n' "$body" > "$path"
}

join_by() {
  local separator="$1"
  shift
  local first="true"
  local item
  for item in "$@"; do
    if [[ "$first" == "true" ]]; then
      printf '%s' "$item"
      first="false"
    else
      printf '%s%s' "$separator" "$item"
    fi
  done
}

build_secret_refs_yaml() {
  local output=""
  local secret_ref
  for secret_ref in "${SECRET_REFS[@]}"; do
    output+="    - ${secret_ref}"$'\n'
  done

  printf '%s' "${output%$'\n'}"
}

build_target_yaml() {
  printf '      - %s' "$RUNTIME_TARGET"
}

build_allowed_environment_yaml() {
  local output=""
  local environment
  for environment in "${ENVIRONMENTS[@]}"; do
    output+="    - ${environment}"$'\n'
  done

  printf '%s' "${output%$'\n'}"
}

build_platform_stack() {
  local environment="$1"
  cat <<EOF
apiVersion: honua.io/v1alpha1
kind: PlatformStack
metadata:
  name: ${PLATFORM_STACK_PREFIX}-${environment}
  namespace: ${environment}
  labels:
    managed-by: honua-devops
    environment: ${environment}
spec:
  environment: ${environment}
  terraform:
    repository: ${TERRAFORM_REPOSITORY}
    ref: ${TERRAFORM_REF}
    targets:
$(build_target_yaml)
  secretRefs:
$(build_secret_refs_yaml)
EOF
}

build_execution_policy_default() {
  cat <<EOF
apiVersion: honua.io/v1alpha1
kind: ExecutionPolicy
metadata:
  name: ${EXECUTION_POLICY_DEFAULT_NAME}
  namespace: ${CONTROL_PLANE_NAMESPACE}
  labels:
    managed-by: honua-devops
    policy-class: default
spec:
  executionMode: plan
  executionTier: plan
  allowedEnvironments:
$(build_allowed_environment_yaml)
  requiredChecks:
    - manifest-diff
    - smoke-contract
    - release-evidence
    - approval-policy
  requiresApproval: true
  allowsBreakGlass: false
EOF
}

build_execution_policy_break_glass() {
  cat <<EOF
apiVersion: honua.io/v1alpha1
kind: ExecutionPolicy
metadata:
  name: ${EXECUTION_POLICY_BREAK_GLASS_NAME}
  namespace: ${CONTROL_PLANE_NAMESPACE}
  labels:
    managed-by: honua-devops
    policy-class: break-glass
spec:
  executionMode: execute
  executionTier: break-glass
  allowedEnvironments:
$(build_allowed_environment_yaml)
  requiredChecks:
    - incident-context
    - operator-justification
    - rollback-intent
    - post-action-review
  requiresApproval: false
  allowsBreakGlass: true
EOF
}

build_release_action() {
  local index="$1"
  local last_index="$2"
  if [[ "$index" -eq "$last_index" && "$last_index" -gt 0 ]]; then
    printf 'promote'
  else
    printf 'sync'
  fi
}

build_change_summary() {
  local environment="$1"
  local index="$2"
  if [[ "$index" -eq 0 ]]; then
    printf 'Initial bootstrap rollout for %s in %s.' "$SERVICE" "$environment"
  else
    printf 'Promote validated %s revision into %s.' "$SERVICE" "$environment"
  fi
}

build_platform_release() {
  local environment="$1"
  local action="$2"
  local change_summary="$3"
  local release_name
  release_name="$(render_name_template \
    "$PLATFORM_RELEASE_NAME_TEMPLATE" \
    "service=$SERVICE_TOKEN" \
    "environment=$environment" \
    "revision=$REVISION_TOKEN")"
  cat <<EOF
apiVersion: honua.io/v1alpha1
kind: PlatformRelease
metadata:
  name: ${release_name}
  namespace: ${environment}
  labels:
    managed-by: honua-devops
    service: ${SERVICE}
    environment: ${environment}
spec:
  service: ${SERVICE}
  environment: ${environment}
  revision: ${REVISION}
  action: ${action}
  changeSummary: ${change_summary}
  gitOpsTool: ${GITOPS_TOOL}
  terraform:
    repository: ${TERRAFORM_REPOSITORY}
    ref: ${TERRAFORM_REF}
    targets:
$(build_target_yaml)
EOF
}

build_promotion() {
  local source_environment="$1"
  local target_environment="$2"
  local promotion_name
  promotion_name="$(render_name_template \
    "$PROMOTION_NAME_TEMPLATE" \
    "service=$SERVICE_TOKEN" \
    "source=$source_environment" \
    "target=$target_environment")"
  cat <<EOF
apiVersion: honua.io/v1alpha1
kind: Promotion
metadata:
  name: ${promotion_name}
  namespace: ${target_environment}
  labels:
    managed-by: honua-devops
    service: ${SERVICE}
spec:
  service: ${SERVICE}
  sourceEnvironment: ${source_environment}
  targetEnvironment: ${target_environment}
  revision: ${REVISION}
  executionPolicyRef:
    apiVersion: honua.io/v1alpha1
    kind: ExecutionPolicy
    name: ${EXECUTION_POLICY_DEFAULT_NAME}
    namespace: ${CONTROL_PLANE_NAMESPACE}
EOF
}

build_service_bundle() {
  local environment="$1"
  local action="$2"
  local change_summary="$3"
  local promotion_block="${4:-}"
  local service_bundle_name
  local platform_release_name
  service_bundle_name="$(render_name_template \
    "$SERVICE_BUNDLE_NAME_TEMPLATE" \
    "service=$SERVICE_TOKEN" \
    "environment=$environment")"
  platform_release_name="$(render_name_template \
    "$PLATFORM_RELEASE_NAME_TEMPLATE" \
    "service=$SERVICE_TOKEN" \
    "environment=$environment" \
    "revision=$REVISION_TOKEN")"
  cat <<EOF
apiVersion: honua.io/v1alpha1
kind: ServiceBundle
metadata:
  name: ${service_bundle_name}
  namespace: ${environment}
  labels:
    managed-by: honua-devops
    service: ${SERVICE}
    environment: ${environment}
spec:
  description: Bootstrap ServiceBundle for ${SERVICE} in ${environment}.
  srid: 4326
  deployment:
    service: ${SERVICE}
    environment: ${environment}
    revision: ${REVISION}
    action: ${action}
    changeSummary: ${change_summary}
    gitOpsTool: ${GITOPS_TOOL}
    terraform:
      repository: ${TERRAFORM_REPOSITORY}
      ref: ${TERRAFORM_REF}
      targets:
$(build_target_yaml)
  relationships:
    platformStackRef:
      apiVersion: honua.io/v1alpha1
      kind: PlatformStack
      name: ${PLATFORM_STACK_PREFIX}-${environment}
      namespace: ${environment}
    platformReleaseRef:
      apiVersion: honua.io/v1alpha1
      kind: PlatformRelease
      name: ${platform_release_name}
      namespace: ${environment}
    executionPolicyRef:
      apiVersion: honua.io/v1alpha1
      kind: ExecutionPolicy
      name: ${EXECUTION_POLICY_DEFAULT_NAME}
      namespace: ${CONTROL_PLANE_NAMESPACE}${promotion_block}
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
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
    --revision)
      require_value "$1" "${2:-}"
      REVISION="$2"
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
    --environments)
      require_value "$1" "${2:-}"
      ENVIRONMENTS_CSV="$2"
      shift 2
      ;;
    --output-root)
      require_value "$1" "${2:-}"
      OUTPUT_ROOT="$2"
      shift 2
      ;;
    --secret-ref)
      require_value "$1" "${2:-}"
      SECRET_REFS+=("$2")
      shift 2
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

validate_service_name "$SERVICE"
validate_runtime_target "$RUNTIME_TARGET"

if [[ "${#SECRET_REFS[@]}" -eq 0 ]]; then
  SECRET_REFS=("HONUA_ADMIN_API_KEY" "HONUA_DB_CONNECTION")
fi

declare -a ENVIRONMENTS=()
declare -A ENVIRONMENT_SEEN=()
IFS=',' read -r -a raw_environments <<< "$ENVIRONMENTS_CSV"
for raw_environment in "${raw_environments[@]}"; do
  environment="$(trim "$raw_environment")"
  if [[ -z "$environment" ]]; then
    continue
  fi

  validate_environment "$environment"
  if [[ -n "${ENVIRONMENT_SEEN[$environment]:-}" ]]; then
    continue
  fi

  ENVIRONMENT_SEEN["$environment"]="true"
  ENVIRONMENTS+=("$environment")
done

if [[ "${#ENVIRONMENTS[@]}" -eq 0 ]]; then
  echo "[ERROR] at least one environment is required" >&2
  exit 1
fi

SERVICE_TOKEN="$(normalize_token "$SERVICE" "service")"
REVISION_TOKEN="$(normalize_token "$REVISION" "revision")"
LAST_INDEX=$((${#ENVIRONMENTS[@]} - 1))

case "$OUTPUT_ROOT" in
  /*) ;;
  *) OUTPUT_ROOT="$REPO_ROOT/$OUTPUT_ROOT" ;;
esac

write_file \
  "$OUTPUT_ROOT/conventions.env" \
  "$(cat "$CONVENTIONS_FILE")"
write_file \
  "$OUTPUT_ROOT/execution-policies/default.executionpolicy.yaml" \
  "$(build_execution_policy_default)"
write_file \
  "$OUTPUT_ROOT/execution-policies/break-glass.executionpolicy.yaml" \
  "$(build_execution_policy_break_glass)"

for index in "${!ENVIRONMENTS[@]}"; do
  environment="${ENVIRONMENTS[$index]}"
  action="$(build_release_action "$index" "$LAST_INDEX")"
  change_summary="$(build_change_summary "$environment" "$index")"

  write_file \
    "$OUTPUT_ROOT/platform-stacks/${environment}.platformstack.yaml" \
    "$(build_platform_stack "$environment")"
  write_file \
    "$OUTPUT_ROOT/releases/${SERVICE_TOKEN}/${environment}.platformrelease.yaml" \
    "$(build_platform_release "$environment" "$action" "$change_summary")"

  promotion_block=""
  if [[ "$index" -gt 0 ]]; then
    previous_environment="${ENVIRONMENTS[$((index - 1))]}"
    promotion_name="$(render_name_template \
      "$PROMOTION_NAME_TEMPLATE" \
      "service=$SERVICE_TOKEN" \
      "source=$previous_environment" \
      "target=$environment")"
    write_file \
      "$OUTPUT_ROOT/promotions/${SERVICE_TOKEN}/${previous_environment}-to-${environment}.promotion.yaml" \
      "$(build_promotion "$previous_environment" "$environment")"
    promotion_block=$'\n'"    promotionRef:"$'\n'"      apiVersion: honua.io/v1alpha1"$'\n'"      kind: Promotion"$'\n'"      name: ${promotion_name}"$'\n'"      namespace: ${environment}"
  fi

  write_file \
    "$OUTPUT_ROOT/bundles/${SERVICE_TOKEN}/${environment}.servicebundle.yaml" \
    "$(build_service_bundle "$environment" "$action" "$change_summary" "$promotion_block")"
done

printf 'Scaffolded desired-state for service `%s` targeting `%s` in `%s`\n' \
  "$SERVICE" \
  "$RUNTIME_TARGET" \
  "$(join_by ',' "${ENVIRONMENTS[@]}")"
printf 'Output root: %s\n' "$OUTPUT_ROOT"
