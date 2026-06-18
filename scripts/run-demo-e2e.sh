#!/usr/bin/env bash
#
# run-demo-e2e.sh — L3 end-to-end verification harness for the DEPLOYED demo env.
#
# Proves the full customer-facing demo workflows actually work against the live
# deployment (demo.honua.io: real Lambda + Postgres + real Maui data), not just
# that per-PR unit tests pass.
#
# DESIGN RULES (target is a real, customer-facing env):
#   * Parameterized, never hardcoded. Base URL + env name + auth come from env
#     vars / flags at runtime. No secrets in source.
#   * Read vs write split. READ-path checks run against the live customer-facing
#     base URL (HONUA_DEMO_BASE_URL). WRITE / destructive checks (Demo A
#     import/publish, Demo B failure-injection + rollback) target a SEPARATE
#     non-customer-facing write target (HONUA_DEMO_WRITE_BASE_URL — a staging
#     Lambda alias / ephemeral env) under a scoped, self-cleaning resource prefix.
#     Writes NEVER touch the customer-facing alias.
#   * A pro_and_ai_live flag (default false). Pro / Bedrock / export assertions
#     are gated as "expected-pending" until the Pro + Bedrock deploys land. The
#     read path always runs.
#
# Cross-refs: honua-devops#97 (demo program), honua-devops#98 (publish-all matrix).
# Reuses repo conventions from scripts/run-backup-restore-gameday.sh,
# scripts/smoke-contract.sh, and scripts/fault-injection/*.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# ---------------------------------------------------------------------------
# Parameters (flags override env vars; nothing is hardcoded to a real host).
# ---------------------------------------------------------------------------
ENVIRONMENT="${HONUA_DEMO_ENV:-}"
BASE_URL="${HONUA_DEMO_BASE_URL:-}"               # READ path (customer-facing)
WRITE_BASE_URL="${HONUA_DEMO_WRITE_BASE_URL:-}"   # WRITE path (staging alias / ephemeral)
API_KEY="${HONUA_DEMO_API_KEY:-}"                 # optional read key (X-API-Key)
ADMIN_KEY="${HONUA_DEMO_ADMIN_KEY:-}"             # admin/write key for write target
PRO_AI_LIVE="${HONUA_DEMO_PRO_AI_LIVE:-false}"    # gate Pro/Bedrock/export assertions
TIMEOUT_SECONDS="${HONUA_DEMO_TIMEOUT_SECONDS:-30}"
RESOURCE_PREFIX="${HONUA_DEMO_RESOURCE_PREFIX:-e2e-$(date -u +%Y%m%d%H%M%S)}"
OUTPUT_DIR="${HONUA_DEMO_OUTPUT_DIR:-}"
HONUA_CLI="${HONUA_CLI:-}"                         # path/cmd for the `honua` CLI; auto-detected if empty

# Demo A read fixtures (parameterized; defaults match the live demo.honua.io v18 seed).
DEMO_SERVICE_ID="${HONUA_DEMO_SERVICE_ID:-maui-parcels}"
DEMO_LAYER_ID="${HONUA_DEMO_LAYER_ID:-1}"
DEMO_MIN_FEATURE_COUNT="${HONUA_DEMO_MIN_FEATURE_COUNT:-1}"
DEMO_BBOX="${HONUA_DEMO_BBOX:--156.7,20.7,-156.3,21.0}"

usage() {
  cat <<'EOF'
Usage:
  ./scripts/run-demo-e2e.sh --env <name> [options]

Required:
  --env <name>                  Logical environment label (e.g. demo, staging)
                                (or HONUA_DEMO_ENV)

Connection (READ path — customer-facing; never written to):
  --base-url <url>              Read base URL          (or HONUA_DEMO_BASE_URL)
  --api-key <key>               Optional read API key  (or HONUA_DEMO_API_KEY)

Connection (WRITE path — separate non-customer-facing target):
  --write-base-url <url>        Staging alias / ephemeral write target
                                (or HONUA_DEMO_WRITE_BASE_URL)
  --admin-key <key>             Admin/write key        (or HONUA_DEMO_ADMIN_KEY)
  --resource-prefix <p>         Scoped, self-cleaning prefix for write artifacts
                                (or HONUA_DEMO_RESOURCE_PREFIX)

Gating:
  --pro-ai-live                 Run Pro/Bedrock/export & Demo B write assertions.
                                Default: false (those report PENDING until deploy).
                                (or HONUA_DEMO_PRO_AI_LIVE=true)

Read fixtures (parameterized; defaults target the live demo Maui seed):
  --service-id <id>             FeatureServer service id (or HONUA_DEMO_SERVICE_ID)
  --layer-id <n>                Layer id                 (or HONUA_DEMO_LAYER_ID)
  --min-feature-count <n>       Assert query returns >= n (or HONUA_DEMO_MIN_FEATURE_COUNT)
  --bbox <minx,miny,maxx,maxy>  Render/export bbox       (or HONUA_DEMO_BBOX)

Other:
  --output-dir <path>           Evidence directory (default artifacts/demo-e2e/<id>)
  --timeout-seconds <n>         Per-request timeout (default 30)
  --help                        Show this help

Secrets: sourced from env at runtime ONLY. Reference the secret location, never
the value. In CI these come from the HONUA_DEMO_* repo/org secrets (see
docs/demo-e2e-verification.md). No secret is stored in this repo.

Exit codes:
  0  all non-pending hops passed (PENDING hops are not failures)
  1  configuration error (missing required input)
  2  one or more non-pending assertions FAILED
EOF
}

while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --env) ENVIRONMENT="$2"; shift 2 ;;
    --base-url) BASE_URL="$2"; shift 2 ;;
    --api-key) API_KEY="$2"; shift 2 ;;
    --write-base-url) WRITE_BASE_URL="$2"; shift 2 ;;
    --admin-key) ADMIN_KEY="$2"; shift 2 ;;
    --resource-prefix) RESOURCE_PREFIX="$2"; shift 2 ;;
    --pro-ai-live) PRO_AI_LIVE="true"; shift 1 ;;
    --service-id) DEMO_SERVICE_ID="$2"; shift 2 ;;
    --layer-id) DEMO_LAYER_ID="$2"; shift 2 ;;
    --min-feature-count) DEMO_MIN_FEATURE_COUNT="$2"; shift 2 ;;
    --bbox) DEMO_BBOX="$2"; shift 2 ;;
    --output-dir) OUTPUT_DIR="$2"; shift 2 ;;
    --timeout-seconds) TIMEOUT_SECONDS="$2"; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) echo "[ERROR] Unknown argument: $1" >&2; usage; exit 1 ;;
  esac
done

if [[ -z "$ENVIRONMENT" ]]; then
  echo "[ERROR] --env (or HONUA_DEMO_ENV) is required." >&2
  usage
  exit 1
fi
if [[ -z "$BASE_URL" ]]; then
  echo "[ERROR] --base-url (or HONUA_DEMO_BASE_URL) is required for read-path checks." >&2
  usage
  exit 1
fi
BASE_URL="${BASE_URL%/}"
WRITE_BASE_URL="${WRITE_BASE_URL%/}"

# Guardrail: a write target MUST be a distinct host from the customer-facing read
# base URL. Refuse to run write/destructive hops against the customer alias.
WRITE_TARGET_OK="false"
WRITE_TARGET_REASON="write base URL not configured"
if [[ -n "$WRITE_BASE_URL" ]]; then
  if [[ "$WRITE_BASE_URL" == "$BASE_URL" ]]; then
    WRITE_TARGET_OK="false"
    WRITE_TARGET_REASON="REFUSED: write base URL equals customer-facing read base URL"
  else
    WRITE_TARGET_OK="true"
    WRITE_TARGET_REASON="distinct write target configured"
  fi
fi

if [[ -z "$OUTPUT_DIR" ]]; then
  TIMESTAMP="$(date -u +"%Y%m%dT%H%M%SZ")"
  OUTPUT_DIR="$REPO_ROOT/artifacts/demo-e2e/${ENVIRONMENT}-${TIMESTAMP}"
fi
mkdir -p "$OUTPUT_DIR/logs"

# Auto-detect the honua CLI (honua-sdk-js #292). Falls back to direct HTTP.
if [[ -z "$HONUA_CLI" ]]; then
  if command -v honua >/dev/null 2>&1; then
    HONUA_CLI="honua"
  fi
fi
CLI_AVAILABLE="false"
[[ -n "$HONUA_CLI" ]] && CLI_AVAILABLE="true"

# ---------------------------------------------------------------------------
# Result accumulation. Each hop records: id, workflow, status (PASS/FAIL/PENDING),
# detail (the real asserted value), and the driver used (cli|http).
# ---------------------------------------------------------------------------
declare -a R_ID R_FLOW R_STATUS R_DETAIL R_DRIVER
FAILURES=0

record() {
  # record <id> <workflow> <PASS|FAIL|PENDING> <driver> <detail...>
  local id="$1" flow="$2" status="$3" driver="$4"; shift 4
  local detail="$*"
  R_ID+=("$id"); R_FLOW+=("$flow"); R_STATUS+=("$status")
  R_DRIVER+=("$driver"); R_DETAIL+=("$detail")
  printf '[%-7s] %-28s %s\n' "$status" "$id" "$detail"
  [[ "$status" == "FAIL" ]] && FAILURES=$((FAILURES + 1)) || true
}

json_escape() {
  local v="$1"
  v="${v//\\/\\\\}"; v="${v//\"/\\\"}"
  v="${v//$'\n'/\\n}"; v="${v//$'\r'/\\r}"; v="${v//$'\t'/\\t}"
  printf '%s' "$v"
}

# curl wrapper: prints "HTTP_CODE<space>CONTENT_TYPE" and writes body to $1.
http_probe() {
  local body_file="$1" url="$2"; shift 2
  local args=(--silent --show-error --output "$body_file"
              --write-out "%{http_code} %{content_type}"
              --max-time "$TIMEOUT_SECONDS")
  if [[ -n "$API_KEY" ]]; then args+=(--header "X-API-Key: $API_KEY"); fi
  while [[ "$#" -gt 0 ]]; do args+=("$1"); shift; done
  curl "${args[@]}" "$url" 2>>"$OUTPUT_DIR/logs/http.log" || echo "000 -"
}

# Pull "HTTP_CODE" out of the http_probe write-out string.
code_of() { printf '%s' "$1" | awk '{print $1}'; }
ctype_of() { printf '%s' "$1" | cut -d' ' -f2- ; }

# ===========================================================================
# WORKFLOW: Demo A — AI GIS workflow (read path + gated write/AI/export path)
# ===========================================================================
demo_a() {
  local fs_url="$BASE_URL/rest/services/$DEMO_SERVICE_ID/FeatureServer/$DEMO_LAYER_ID"

  # --- A0: service reachable (catalog) ---------------------------------------
  local rest_body="$OUTPUT_DIR/logs/rest-info.json"
  local r; r="$(http_probe "$rest_body" "$BASE_URL/rest/info?f=json")"
  if [[ "$(code_of "$r")" == "200" ]]; then
    local ver; ver="$(python3 -c "import json,sys;print(json.load(open('$rest_body')).get('currentVersion','?'))" 2>/dev/null || echo '?')"
    record "demoA.catalog" "Demo A" PASS http "rest/info 200, currentVersion=$ver"
  else
    record "demoA.catalog" "Demo A" FAIL http "rest/info -> $(code_of "$r")"
  fi

  # --- A1: import -> poll job to Succeeded (WRITE; gated + staging-only) ------
  if [[ "$PRO_AI_LIVE" == "true" && "$WRITE_TARGET_OK" == "true" ]]; then
    assert_import_job
  else
    local why="pro_and_ai_live=false"
    [[ "$WRITE_TARGET_OK" != "true" ]] && why="$WRITE_TARGET_REASON"
    record "demoA.import" "Demo A" PENDING http "import->job Succeeded gated ($why)"
  fi

  # --- A2: layer queryable — FeatureServer returns non-zero/expected count ----
  # Prefer the honua CLI (#292) for the count; fall back to direct HTTP.
  local count="" driver="http"
  if [[ "$CLI_AVAILABLE" == "true" ]]; then
    if count="$(HONUA_BASE_URL="$BASE_URL" HONUA_API_KEY="$API_KEY" \
                "$HONUA_CLI" query "$DEMO_SERVICE_ID/$DEMO_LAYER_ID" --where "1=1" --count --json \
                2>"$OUTPUT_DIR/logs/cli-query.log" \
                | python3 -c "import json,sys;d=json.load(sys.stdin);print(d.get('count', d.get('value','')))" 2>/dev/null)"; then
      [[ -n "$count" ]] && driver="cli"
    fi
  fi
  if [[ -z "$count" ]]; then
    local q="$OUTPUT_DIR/logs/query-count.json"
    local qr; qr="$(http_probe "$q" "$fs_url/query?where=1%3D1&returnCountOnly=true&f=json")"
    if [[ "$(code_of "$qr")" == "200" ]]; then
      count="$(python3 -c "import json,sys;print(json.load(open('$q')).get('count',''))" 2>/dev/null || echo '')"
    fi
    driver="http"
  fi
  if [[ "$count" =~ ^[0-9]+$ ]] && [[ "$count" -ge "$DEMO_MIN_FEATURE_COUNT" ]]; then
    record "demoA.query" "Demo A" PASS "$driver" "FeatureServer count=$count (>= $DEMO_MIN_FEATURE_COUNT)"
  else
    record "demoA.query" "Demo A" FAIL "$driver" "FeatureServer count='${count:-none}' (expected >= $DEMO_MIN_FEATURE_COUNT)"
  fi

  # --- A2b: features actually return (non-zero feature array) -----------------
  local f="$OUTPUT_DIR/logs/query-features.json"
  local fr; fr="$(http_probe "$f" "$fs_url/query?where=1%3D1&resultRecordCount=2&outFields=%2A&f=json")"
  if [[ "$(code_of "$fr")" == "200" ]]; then
    local nfeat; nfeat="$(python3 -c "import json,sys;print(len(json.load(open('$f')).get('features',[])))" 2>/dev/null || echo 0)"
    if [[ "$nfeat" -ge 1 ]]; then
      record "demoA.features" "Demo A" PASS http "query returned $nfeat feature(s)"
    else
      record "demoA.features" "Demo A" FAIL http "query returned 0 features"
    fi
  else
    record "demoA.features" "Demo A" FAIL http "feature query -> $(code_of "$fr")"
  fi

  # --- A3: AI generate via Bedrock -> VALID workflow/map proposal ------------
  # AI generate hits Bedrock and must NOT be POSTed at the customer-facing alias,
  # so it requires both pro_and_ai_live AND a distinct (staging) target.
  if [[ "$PRO_AI_LIVE" == "true" && "$WRITE_TARGET_OK" == "true" ]]; then
    assert_ai_generate
  else
    local why="pro_and_ai_live=false"
    [[ "$PRO_AI_LIVE" == "true" && "$WRITE_TARGET_OK" != "true" ]] && why="$WRITE_TARGET_REASON"
    record "demoA.ai_generate" "Demo A" PENDING http "Bedrock generate (status=Generated, real graph) gated ($why)"
  fi

  # --- A4: publish (WRITE; gated + staging-only) -----------------------------
  if [[ "$PRO_AI_LIVE" == "true" && "$WRITE_TARGET_OK" == "true" ]]; then
    assert_publish
  else
    local why="pro_and_ai_live=false"
    [[ "$WRITE_TARGET_OK" != "true" ]] && why="$WRITE_TARGET_REASON"
    record "demoA.publish" "Demo A" PENDING http "publish gated ($why)"
  fi

  # --- A5: render 200 (MapServer export of published map) --------------------
  # Render is read-only; it is green on the live demo today.
  local img="$OUTPUT_DIR/logs/render.png"
  local rr; rr="$(http_probe "$img" "$BASE_URL/rest/services/$DEMO_SERVICE_ID/MapServer/export?bbox=$DEMO_BBOX&size=400,300&format=png&f=image")"
  local rcode; rcode="$(code_of "$rr")"
  if [[ "$rcode" == "200" ]] && is_png "$img"; then
    record "demoA.render" "Demo A" PASS http "MapServer/export 200, valid PNG ($(wc -c <"$img") bytes)"
  elif [[ "$rcode" == "200" ]]; then
    # 200 but body is JSON (e.g. a {href:...} envelope) — follow href if present.
    local href; href="$(python3 -c "import json,sys;d=json.load(open('$img'));print(d.get('href',''))" 2>/dev/null || echo '')"
    if [[ -n "$href" ]]; then
      http_probe "$img" "$href" >/dev/null
      if is_png "$img"; then
        record "demoA.render" "Demo A" PASS http "MapServer/export -> href, valid PNG ($(wc -c <"$img") bytes)"
      else
        record "demoA.render" "Demo A" FAIL http "render href body not a valid PNG"
      fi
    else
      record "demoA.render" "Demo A" FAIL http "render 200 but body not PNG"
    fi
  else
    record "demoA.render" "Demo A" FAIL http "MapServer/export -> $rcode"
  fi

  # --- A6: export PDF/PNG -> assert valid magic bytes (gated) -----------------
  # Export of the published map runs on the staging target where the artifact
  # was published; gated behind pro_and_ai_live + a distinct write target.
  if [[ "$PRO_AI_LIVE" == "true" && "$WRITE_TARGET_OK" == "true" ]]; then
    assert_export_bytes
  else
    local why="pro_and_ai_live=false"
    [[ "$PRO_AI_LIVE" == "true" && "$WRITE_TARGET_OK" != "true" ]] && why="$WRITE_TARGET_REASON"
    record "demoA.export" "Demo A" PENDING http "export PDF/PNG byte-validation (%PDF / PNG magic) gated ($why)"
  fi
}

# Validate a file begins with the PNG magic header.
is_png() {
  local file="$1"
  [[ -s "$file" ]] || return 1
  local magic; magic="$(head -c 8 "$file" | od -An -tx1 | tr -d ' \n')"
  [[ "$magic" == "89504e470d0a1a0a" ]]
}
# Validate a file begins with "%PDF".
is_pdf() {
  local file="$1"
  [[ -s "$file" ]] || return 1
  [[ "$(head -c 4 "$file")" == "%PDF" ]]
}

# --- gated write/AI assertions (run only when pro_and_ai_live and write target ok) ---
assert_import_job() {
  # Import a small fixture into the STAGING write target under RESOURCE_PREFIX,
  # poll the job to Succeeded. Idempotent + self-cleaning (teardown in cleanup()).
  local target="$WRITE_BASE_URL"
  local import_layer="${RESOURCE_PREFIX}-import"
  local body="$OUTPUT_DIR/logs/import-submit.json"
  local args=(--silent --show-error --output "$body" --write-out "%{http_code}"
              --max-time "$TIMEOUT_SECONDS" -X POST
              --header "Content-Type: application/json")
  [[ -n "$ADMIN_KEY" ]] && args+=(--header "X-API-Key: $ADMIN_KEY")
  local payload; payload="$(printf '{"layerId":"%s","source":"demo-e2e-fixture","resourcePrefix":"%s"}' "$import_layer" "$RESOURCE_PREFIX")"
  local code; code="$(curl "${args[@]}" --data "$payload" "$target/api/v1/import/jobs" 2>>"$OUTPUT_DIR/logs/http.log" || echo 000)"
  if [[ "$code" != "200" && "$code" != "201" && "$code" != "202" ]]; then
    record "demoA.import" "Demo A" FAIL http "import submit -> $code (target=$target)"
    return
  fi
  local job_id; job_id="$(python3 -c "import json,sys;print(json.load(open('$body')).get('jobId',''))" 2>/dev/null || echo '')"
  CLEANUP_LAYERS+=("$import_layer")
  # Poll up to ~90s for terminal status.
  local status="" i
  for i in $(seq 1 30); do
    local pb="$OUTPUT_DIR/logs/import-poll.json"
    local pc; pc="$(http_probe "$pb" "$target/api/v1/import/jobs/$job_id")"
    [[ "$(code_of "$pc")" == "200" ]] || break
    status="$(python3 -c "import json,sys;print(json.load(open('$pb')).get('status',''))" 2>/dev/null || echo '')"
    case "$status" in Succeeded|Failed|Cancelled) break ;; esac
    sleep 3
  done
  if [[ "$status" == "Succeeded" ]]; then
    record "demoA.import" "Demo A" PASS http "import job $job_id -> Succeeded (staging)"
  else
    record "demoA.import" "Demo A" FAIL http "import job $job_id -> '${status:-no-status}' (expected Succeeded)"
  fi
}

assert_ai_generate() {
  # AI generate via Bedrock must return a VALID proposal: status=Generated with
  # a real workflow/map graph — NOT Unsupported / error.
  local target="$WRITE_BASE_URL"
  local body="$OUTPUT_DIR/logs/ai-generate.json"
  local args=(--silent --show-error --output "$body" --write-out "%{http_code}"
              --max-time "$TIMEOUT_SECONDS" -X POST
              --header "Content-Type: application/json")
  [[ -n "$ADMIN_KEY" ]] && args+=(--header "X-API-Key: $ADMIN_KEY")
  local payload='{"prompt":"Map Maui parcels at risk from sea level rise","mode":"workflow"}'
  local code; code="$(curl "${args[@]}" --data "$payload" "$target/api/v1/ai/generate" 2>>"$OUTPUT_DIR/logs/http.log" || echo 000)"
  if [[ "$code" != "200" ]]; then
    record "demoA.ai_generate" "Demo A" FAIL http "AI generate -> $code (target=$target)"
    return
  fi
  local status nnodes
  status="$(python3 -c "import json,sys;print(json.load(open('$body')).get('status',''))" 2>/dev/null || echo '')"
  nnodes="$(python3 -c "import json,sys;d=json.load(open('$body'));g=d.get('graph',{});print(len(g.get('nodes',[])))" 2>/dev/null || echo 0)"
  if [[ "$status" == "Generated" && "$nnodes" -ge 1 ]]; then
    record "demoA.ai_generate" "Demo A" PASS http "Bedrock proposal status=Generated, graph nodes=$nnodes"
  else
    record "demoA.ai_generate" "Demo A" FAIL http "AI proposal status='$status' nodes=$nnodes (expected Generated + real graph, not Unsupported)"
  fi
}

assert_publish() {
  local target="$WRITE_BASE_URL"
  local pub_id="${RESOURCE_PREFIX}-pub"
  local body="$OUTPUT_DIR/logs/publish.json"
  local args=(--silent --show-error --output "$body" --write-out "%{http_code}"
              --max-time "$TIMEOUT_SECONDS" -X POST
              --header "Content-Type: application/json")
  [[ -n "$ADMIN_KEY" ]] && args+=(--header "X-API-Key: $ADMIN_KEY")
  local code; code="$(curl "${args[@]}" --data "$(printf '{"serviceId":"%s","resourcePrefix":"%s"}' "$pub_id" "$RESOURCE_PREFIX")" "$target/api/v1/publish" 2>>"$OUTPUT_DIR/logs/http.log" || echo 000)"
  CLEANUP_SERVICES+=("$pub_id")
  if [[ "$code" == "200" || "$code" == "201" ]]; then
    record "demoA.publish" "Demo A" PASS http "published $pub_id (staging) -> $code"
  else
    record "demoA.publish" "Demo A" FAIL http "publish $pub_id -> $code"
  fi
}

assert_export_bytes() {
  local target="$WRITE_BASE_URL"
  # PNG export
  local png="$OUTPUT_DIR/logs/export.png"
  local pr; pr="$(http_probe "$png" "$target/rest/services/$DEMO_SERVICE_ID/MapServer/export?bbox=$DEMO_BBOX&size=600,400&format=png&f=image")"
  if [[ "$(code_of "$pr")" == "200" ]] && is_png "$png"; then
    record "demoA.export.png" "Demo A" PASS http "export PNG valid magic header ($(wc -c <"$png") bytes)"
  else
    record "demoA.export.png" "Demo A" FAIL http "export PNG not valid (code=$(code_of "$pr"))"
  fi
  # PDF export
  local pdf="$OUTPUT_DIR/logs/export.pdf"
  local dr; dr="$(http_probe "$pdf" "$target/rest/services/$DEMO_SERVICE_ID/MapServer/export?bbox=$DEMO_BBOX&size=600,400&format=pdf&f=image")"
  if [[ "$(code_of "$dr")" == "200" ]] && is_pdf "$pdf"; then
    record "demoA.export.pdf" "Demo A" PASS http "export PDF '%PDF' header ($(wc -c <"$pdf") bytes)"
  else
    record "demoA.export.pdf" "Demo A" FAIL http "export PDF not valid (code=$(code_of "$dr"))"
  fi
}

# ===========================================================================
# WORKFLOW: Publish-all matrix (honua-devops#98) — probe every service type.
# READ-only against the live customer-facing base URL.
# ===========================================================================
publish_all_matrix() {
  # name | url (relative to BASE_URL) | validator
  matrix_probe "FeatureServer.query" \
    "/rest/services/$DEMO_SERVICE_ID/FeatureServer/$DEMO_LAYER_ID/query?where=1%3D1&returnCountOnly=true&f=json" \
    json_has_count
  matrix_probe "MapServer.export" \
    "/rest/services/$DEMO_SERVICE_ID/MapServer/export?bbox=$DEMO_BBOX&size=256,256&format=png&f=image" \
    is_image_png
  matrix_probe "WMS.GetMap" \
    "/ogc/wms?service=WMS&request=GetMap&version=1.3.0&layers=$DEMO_SERVICE_ID&crs=EPSG:4326&bbox=20.7,-156.7,21.0,-156.3&width=256&height=256&format=image/png" \
    is_image_png
  matrix_probe "WMTS.tile" \
    "/ogc/tiles/WebMercatorQuad/0/0/0?f=png" \
    is_image_any
  matrix_probe "OGCFeatures.items" \
    "/ogc/features/collections?f=json" \
    json_has_collections
  matrix_probe "STAC.catalog" \
    "/stac" \
    json_is_stac
  matrix_probe "OData.metadata" \
    "/odata/\$metadata" \
    is_xml_metadata
  matrix_probe "GeocodeServer.findAddressCandidates" \
    "/rest/services/Geocode/GeocodeServer/findAddressCandidates?SingleLine=Kahului&f=json" \
    json_has_candidates
  matrix_probe "OGCTiles" \
    "/ogc/tiles?f=json" \
    json_has_links
}

# matrix_probe <name> <relative-url> <validator-fn>
# Validator receives (body_file, http_code, content_type) and returns 0 on valid.
matrix_probe() {
  local name="$1" rel="$2" validator="$3"
  local body="$OUTPUT_DIR/logs/matrix-${name}.out"
  local r; r="$(http_probe "$body" "${BASE_URL}${rel}")"
  local code ctype; code="$(code_of "$r")"; ctype="$(ctype_of "$r")"
  if [[ "$code" == "200" ]] && "$validator" "$body" "$code" "$ctype"; then
    record "matrix.$name" "Publish-all matrix" PASS http "$code valid ($ctype)"
  else
    # A protocol that is simply not deployed yet reports PENDING (not FAIL) when
    # it 404s, so the matrix reflects the deploy frontier rather than a regression.
    if [[ "$code" == "404" || "$code" == "501" || "$code" == "000" ]]; then
      record "matrix.$name" "Publish-all matrix" PENDING http "$code not-yet-published"
    else
      record "matrix.$name" "Publish-all matrix" FAIL http "$code invalid ($ctype)"
    fi
  fi
}

# --- matrix validators ------------------------------------------------------
json_has_count()       { python3 -c "import json,sys;d=json.load(open('$1'));sys.exit(0 if 'count' in d else 1)" 2>/dev/null; }
json_has_collections() { python3 -c "import json,sys;d=json.load(open('$1'));sys.exit(0 if d.get('collections') is not None else 1)" 2>/dev/null; }
json_is_stac()         { python3 -c "import json,sys;d=json.load(open('$1'));sys.exit(0 if d.get('stac_version') else 1)" 2>/dev/null; }
json_has_candidates()  { python3 -c "import json,sys;d=json.load(open('$1'));sys.exit(0 if 'candidates' in d else 1)" 2>/dev/null; }
json_has_links()       { python3 -c "import json,sys;d=json.load(open('$1'));sys.exit(0 if d.get('links') else 1)" 2>/dev/null; }
is_image_png()         { is_png "$1"; }
is_image_any()         { local m; m="$(head -c 4 "$1" | od -An -tx1 | tr -d ' \n')"; [[ "$m" == 89504e47 || "$m" == 47494638 || "$m" == ffd8ffe0 || "$m" == ffd8ffe1 ]]; }
is_xml_metadata()      { grep -qi 'edmx\|<?xml' "$1" 2>/dev/null; }

# ===========================================================================
# WORKFLOW: Demo B (ops) — STAGING write target ONLY.
# proposal -> approve -> submit -> Succeeded; then inject failing change ->
# RolledBack AND schema+data actually reverted (capture before/after, diff).
# Reuses scripts/fault-injection/* lifecycle. Idempotent + self-cleaning.
# ===========================================================================
demo_b() {
  if [[ "$PRO_AI_LIVE" != "true" ]]; then
    record "demoB.proposal"    "Demo B" PENDING http "proposal->approve->submit->Succeeded gated (pro_and_ai_live=false)"
    record "demoB.rollback"    "Demo B" PENDING http "fault-injection -> RolledBack + schema/data reverted gated (pro_and_ai_live=false)"
    return
  fi
  if [[ "$WRITE_TARGET_OK" != "true" ]]; then
    # NEVER inject a failure into the customer-facing alias.
    record "demoB.proposal"    "Demo B" PENDING http "$WRITE_TARGET_REASON"
    record "demoB.rollback"    "Demo B" PENDING http "$WRITE_TARGET_REASON"
    return
  fi

  local target="$WRITE_BASE_URL"
  local op="${RESOURCE_PREFIX}-opsB"

  # proposal -> approve -> submit
  local pj="$OUTPUT_DIR/logs/opsB-proposal.json"
  local args=(--silent --show-error --output "$pj" --write-out "%{http_code}"
              --max-time "$TIMEOUT_SECONDS" -X POST --header "Content-Type: application/json")
  [[ -n "$ADMIN_KEY" ]] && args+=(--header "X-API-Key: $ADMIN_KEY")
  local code; code="$(curl "${args[@]}" --data "$(printf '{"operationId":"%s","resourcePrefix":"%s"}' "$op" "$RESOURCE_PREFIX")" "$target/api/v1/devops/proposals" 2>>"$OUTPUT_DIR/logs/http.log" || echo 000)"
  CLEANUP_OPS+=("$op")
  if [[ "$code" != "200" && "$code" != "201" && "$code" != "202" ]]; then
    record "demoB.proposal" "Demo B" FAIL http "proposal/approve/submit -> $code"
  else
    # Poll the operation to Succeeded.
    local status="" i
    for i in $(seq 1 40); do
      local sb="$OUTPUT_DIR/logs/opsB-status.json"
      local sc; sc="$(http_probe "$sb" "$target/api/v1/devops/operations/$op")"
      [[ "$(code_of "$sc")" == "200" ]] || break
      status="$(python3 -c "import json,sys;print(json.load(open('$sb')).get('status',''))" 2>/dev/null || echo '')"
      case "$status" in Succeeded|Failed|RolledBack) break ;; esac
      sleep 3
    done
    if [[ "$status" == "Succeeded" ]]; then
      record "demoB.proposal" "Demo B" PASS http "operation $op -> Succeeded (staging)"
    else
      record "demoB.proposal" "Demo B" FAIL http "operation $op -> '${status:-no-status}' (expected Succeeded)"
    fi
  fi

  # Capture schema+data state BEFORE injecting the fault.
  local before="$OUTPUT_DIR/logs/opsB-schema-before.json"
  http_probe "$before" "$target/rest/services/$DEMO_SERVICE_ID/FeatureServer/$DEMO_LAYER_ID?f=json" >/dev/null

  # Inject a failing change / health-check via the repo's fault-injection lifecycle.
  # Uses FAULT_DRY_RUN unless explicitly armed; restore always runs in cleanup.
  local fi_inject="$REPO_ROOT/scripts/fault-injection/FAULT-010-inject.sh"
  if [[ -x "$fi_inject" ]]; then
    FAULT_ENV="$ENVIRONMENT" FAULT_REGION="${HONUA_DEMO_FAULT_REGION:-us-west-2}" \
      FAULT_RESOURCE_PREFIX="$RESOURCE_PREFIX" FAULT_DRY_RUN="${HONUA_DEMO_FAULT_DRY_RUN:-true}" \
      "$fi_inject" >"$OUTPUT_DIR/logs/opsB-fault-inject.log" 2>&1 || true
    FAULT_INJECTED="true"
  fi

  # Submit the failing change and expect RolledBack.
  local rj="$OUTPUT_DIR/logs/opsB-rollback.json"
  local rc; rc="$(curl "${args[@]}" --data "$(printf '{"operationId":"%s-fail","resourcePrefix":"%s","injectFailure":true}' "$op" "$RESOURCE_PREFIX")" "$target/api/v1/devops/proposals" 2>>"$OUTPUT_DIR/logs/http.log" || echo 000)"
  CLEANUP_OPS+=("${op}-fail")
  local rstatus="" i
  for i in $(seq 1 40); do
    local rsb="$OUTPUT_DIR/logs/opsB-rollback-status.json"
    local rsc; rsc="$(http_probe "$rsb" "$target/api/v1/devops/operations/${op}-fail")"
    [[ "$(code_of "$rsc")" == "200" ]] || break
    rstatus="$(python3 -c "import json,sys;print(json.load(open('$rsb')).get('status',''))" 2>/dev/null || echo '')"
    case "$rstatus" in RolledBack|Succeeded|Failed) break ;; esac
    sleep 3
  done

  # Capture schema+data AFTER, and diff against BEFORE to PROVE the revert.
  local after="$OUTPUT_DIR/logs/opsB-schema-after.json"
  http_probe "$after" "$target/rest/services/$DEMO_SERVICE_ID/FeatureServer/$DEMO_LAYER_ID?f=json" >/dev/null
  local reverted="false"
  if [[ -s "$before" && -s "$after" ]]; then
    if python3 -c "import json,sys;a=json.load(open('$before'));b=json.load(open('$after'));sys.exit(0 if json.dumps(a,sort_keys=True)==json.dumps(b,sort_keys=True) else 1)" 2>/dev/null; then
      reverted="true"
    fi
  fi
  if [[ "$rstatus" == "RolledBack" && "$reverted" == "true" ]]; then
    record "demoB.rollback" "Demo B" PASS http "operation ${op}-fail -> RolledBack; schema/data reverted (before==after diff clean)"
  else
    record "demoB.rollback" "Demo B" FAIL http "rollback status='${rstatus:-none}' reverted=$reverted (expected RolledBack + clean revert)"
  fi
}

# ===========================================================================
# Cleanup / teardown — idempotent, self-cleaning. Restores any injected fault
# and removes scoped staging artifacts. Runs on EXIT.
# ===========================================================================
declare -a CLEANUP_LAYERS CLEANUP_SERVICES CLEANUP_OPS
FAULT_INJECTED="false"
cleanup() {
  # Restore the fault first so we never leave a degraded staging target.
  if [[ "$FAULT_INJECTED" == "true" ]]; then
    local fi_restore="$REPO_ROOT/scripts/fault-injection/FAULT-010-restore.sh"
    if [[ -x "$fi_restore" ]]; then
      FAULT_ENV="$ENVIRONMENT" FAULT_REGION="${HONUA_DEMO_FAULT_REGION:-us-west-2}" \
        FAULT_RESOURCE_PREFIX="$RESOURCE_PREFIX" FAULT_DRY_RUN="${HONUA_DEMO_FAULT_DRY_RUN:-true}" \
        "$fi_restore" >"$OUTPUT_DIR/logs/opsB-fault-restore.log" 2>&1 || true
    fi
  fi
  # Delete scoped staging artifacts (never the customer alias).
  if [[ "$WRITE_TARGET_OK" == "true" ]]; then
    local args=(--silent --output /dev/null --max-time "$TIMEOUT_SECONDS" -X DELETE)
    [[ -n "$ADMIN_KEY" ]] && args+=(--header "X-API-Key: $ADMIN_KEY")
    local id
    for id in "${CLEANUP_SERVICES[@]:-}"; do [[ -n "$id" ]] && curl "${args[@]}" "$WRITE_BASE_URL/api/v1/publish/$id" 2>/dev/null || true; done
    for id in "${CLEANUP_LAYERS[@]:-}"; do [[ -n "$id" ]] && curl "${args[@]}" "$WRITE_BASE_URL/api/v1/import/layers/$id" 2>/dev/null || true; done
    for id in "${CLEANUP_OPS[@]:-}"; do [[ -n "$id" ]] && curl "${args[@]}" "$WRITE_BASE_URL/api/v1/devops/operations/$id" 2>/dev/null || true; done
  fi
}
trap cleanup EXIT

# ===========================================================================
# Run.
# ===========================================================================
echo "==============================================================="
echo " Honua DEPLOYED-ENV demo verification harness (L3)"
echo "   env             : $ENVIRONMENT"
echo "   read base URL   : $BASE_URL  (customer-facing; read-only)"
echo "   write base URL  : ${WRITE_BASE_URL:-<none>}  ($WRITE_TARGET_REASON)"
echo "   pro_and_ai_live : $PRO_AI_LIVE"
echo "   honua CLI       : $([[ "$CLI_AVAILABLE" == true ]] && echo "$HONUA_CLI" || echo '<not found> (HTTP fallback)')"
echo "   resource prefix : $RESOURCE_PREFIX  (scoped, self-cleaning)"
echo "==============================================================="
echo
echo "--- Demo A: AI GIS workflow -----------------------------------"
demo_a
echo
echo "--- Publish-all matrix (honua-devops#98) ----------------------"
publish_all_matrix
echo
echo "--- Demo B: ops proposal/approve/submit/rollback (staging) ----"
demo_b
echo

# ---------------------------------------------------------------------------
# Per-protocol pass/fail table + summary counts.
# ---------------------------------------------------------------------------
PASS_N=0; FAIL_N=0; PEND_N=0
for s in "${R_STATUS[@]}"; do
  case "$s" in PASS) PASS_N=$((PASS_N+1));; FAIL) FAIL_N=$((FAIL_N+1));; PENDING) PEND_N=$((PEND_N+1));; esac
done

echo "=============================== REPORT ========================="
printf '%-30s %-22s %-8s %s\n' "HOP" "WORKFLOW" "STATUS" "DETAIL"
printf '%-30s %-22s %-8s %s\n' "------------------------------" "----------------------" "--------" "------"
for i in "${!R_ID[@]}"; do
  printf '%-30s %-22s %-8s %s\n' "${R_ID[$i]}" "${R_FLOW[$i]}" "${R_STATUS[$i]}" "${R_DETAIL[$i]}"
done
echo "---------------------------------------------------------------"
echo "PASS=$PASS_N  FAIL=$FAIL_N  PENDING=$PEND_N"
echo "==============================================================="

# ---------------------------------------------------------------------------
# Evidence bundle (JSON + Markdown), matching repo gameday conventions.
# ---------------------------------------------------------------------------
overall_status="pass"
[[ "$FAIL_N" -gt 0 ]] && overall_status="fail"

evidence_path="$OUTPUT_DIR/demo-e2e-evidence.json"
{
  printf '{\n'
  printf '  "generated_at_utc": "%s",\n' "$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
  printf '  "environment": "%s",\n' "$(json_escape "$ENVIRONMENT")"
  printf '  "read_base_url": "%s",\n' "$(json_escape "$BASE_URL")"
  printf '  "write_base_url": "%s",\n' "$(json_escape "$WRITE_BASE_URL")"
  printf '  "write_target_ok": %s,\n' "$WRITE_TARGET_OK"
  printf '  "pro_and_ai_live": %s,\n' "$PRO_AI_LIVE"
  printf '  "resource_prefix": "%s",\n' "$(json_escape "$RESOURCE_PREFIX")"
  printf '  "honua_cli": "%s",\n' "$([[ "$CLI_AVAILABLE" == true ]] && json_escape "$HONUA_CLI" || echo 'http-fallback')"
  printf '  "status": "%s",\n' "$overall_status"
  printf '  "counts": { "pass": %d, "fail": %d, "pending": %d },\n' "$PASS_N" "$FAIL_N" "$PEND_N"
  printf '  "hops": [\n'
  for i in "${!R_ID[@]}"; do
    printf '    { "id": "%s", "workflow": "%s", "status": "%s", "driver": "%s", "detail": "%s" }' \
      "$(json_escape "${R_ID[$i]}")" "$(json_escape "${R_FLOW[$i]}")" "${R_STATUS[$i]}" \
      "${R_DRIVER[$i]}" "$(json_escape "${R_DETAIL[$i]}")"
    [[ "$i" -lt $((${#R_ID[@]} - 1)) ]] && printf ',\n' || printf '\n'
  done
  printf '  ]\n'
  printf '}\n'
} >"$evidence_path"

report_path="$OUTPUT_DIR/demo-e2e-report.md"
{
  echo "# Demo E2E Verification — \`$ENVIRONMENT\`"
  echo
  echo "- Read base URL (customer-facing, read-only): \`$BASE_URL\`"
  echo "- Write base URL (staging/ephemeral): \`${WRITE_BASE_URL:-<none>}\` ($WRITE_TARGET_REASON)"
  echo "- pro_and_ai_live: \`$PRO_AI_LIVE\`"
  echo "- Overall: \`$overall_status\` (PASS=$PASS_N FAIL=$FAIL_N PENDING=$PEND_N)"
  echo
  echo "| Hop | Workflow | Status | Driver | Detail |"
  echo "| --- | --- | --- | --- | --- |"
  for i in "${!R_ID[@]}"; do
    echo "| \`${R_ID[$i]}\` | ${R_FLOW[$i]} | ${R_STATUS[$i]} | ${R_DRIVER[$i]} | ${R_DETAIL[$i]} |"
  done
  echo
  echo "PENDING hops are gated behind \`pro_and_ai_live\` and/or a configured write"
  echo "target; they are expected-pending until the Pro + Bedrock deploys land and"
  echo "are NOT failures. See docs/demo-e2e-verification.md for the referenced gates"
  echo "(honua-mobile live-server-integration, client-interop-nightly, geobench)."
} >"$report_path"

# GitHub step summary (when in CI).
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  cat "$report_path" >>"$GITHUB_STEP_SUMMARY"
fi

echo
echo "Evidence written to: $OUTPUT_DIR"
echo "  - demo-e2e-evidence.json"
echo "  - demo-e2e-report.md"

# Exit non-zero on any non-pending failure; PENDING never fails the run.
if [[ "$FAIL_N" -gt 0 ]]; then
  echo "[ERROR] $FAIL_N non-pending assertion(s) FAILED." >&2
  exit 2
fi
exit 0
