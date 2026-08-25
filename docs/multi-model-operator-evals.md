# Multi-Model Operator Evals

This document covers the two operator-eval lanes in this repository:

1. **The multi-model runner** (`scripts/run-multi-model-operator-evals.py`,
   `honua-devops#31`) — scores model lanes against honua-server's deterministic
   server-side eval report. CI runs its fixture smoke path; the lane commands
   themselves are caller-supplied and are not wired to any workflow here.
2. **The blind fault-injection lane** (`--eval-blind`, `honua-devops#155`) — the
   credentialed, scheduled model-behavior signal. It replays the blind
   fault-injection corpus against a live provider and publishes a
   schema-validated `DiagnosisScorecard` artifact pinned to commit SHA and model
   id. See [Blind Fault-Injection Lane](#blind-fault-injection-lane-credentialed).

## Multi-Model Runner

The runner does not invent its own scenario semantics. It consumes:

- the server-side eval report from `honua-server#734`
- scenario fixtures from the shared operator eval corpus
- model lane configuration from `eval/model-matrix.json`

### Runner Contract

Run the contract-only path:

```bash
python3 scripts/run-multi-model-operator-evals.py \
  --server-report ../honua-server/tests/TestResults/eval-report.json \
  --scenario-dir ../honua-server/tests/dotnet/eval/scenarios \
  --hard-fail
```

Run configured model lanes:

```bash
HONUA_EVAL_CODEX_ENABLED=true \
HONUA_EVAL_CODEX_COMMAND='your-codex-eval-command' \
HONUA_EVAL_CLAUDE_ENABLED=true \
HONUA_EVAL_CLAUDE_COMMAND='your-claude-eval-command' \
python3 scripts/run-multi-model-operator-evals.py \
  --run-lanes \
  --hard-fail
```

The runner writes:

- `report.json`
- `report.md`
- per-lane prompt, stdout, stderr, and result artifacts under `lanes/`

Default output path:

```text
artifacts/multi-model-operator-evals/
```

### Lane Matrix

`eval/model-matrix.json` defines four lanes, one per shipped provider
(`ProviderKind`):

| Lane | Role | Release gate |
| --- | --- | --- |
| `claude` | hosted primary model | yes, when enabled |
| `codex` | hosted primary model | yes, when enabled |
| `bedrock` | deployment portability (Claude on Amazon Bedrock) | no |
| `local-llama` | portability/regression model | no |

Local Llama is deliberately not a release authority. It is tracked to catch
portability regressions and prompt overfitting, but hosted release gates remain
Claude and Codex. Its matrix entry uses the same provider id as the runtime
(`local-llama`) and records model, secret, and endpoint env vars so NIM and
other OpenAI-compatible endpoints are configured consistently.

Bedrock is also not a release authority, for a different reason: it hosts the
same model family as the `claude` lane over a different API surface
(`BedrockChatClientAdapter`, the Converse API, the AWS credential chain). It
tracks hosting and adapter regressions, not model-quality regressions, so
counting it as a second release gate would double-count one model. Its
`secretEnv` is optional — with no `HONUA_DEVOPS_BEDROCK_API_KEY` the adapter
uses the AWS credential chain — and `regionEnv` defaults to `us-west-2`.

### Lane Command Environment

Each enabled lane runs once per server scenario. The command receives:

- `HONUA_EVAL_LANE_ID`
- `HONUA_EVAL_SCENARIO_ID`
- `HONUA_EVAL_SCENARIO_MODE`
- `HONUA_EVAL_SCENARIO_PATH`
- `HONUA_EVAL_SERVER_REPORT`
- `HONUA_EVAL_PROMPT_PATH`
- `HONUA_EVAL_LANE_OUTPUT`
- `HONUA_EVAL_MODEL`

The command may also use template tokens directly in the command string:

- `{lane_id}`
- `{scenario_id}`
- `{scenario_path}`
- `{server_report}`
- `{prompt_path}`
- `{lane_output}`
- `{model}`

The command should write JSON to `HONUA_EVAL_LANE_OUTPUT`:

```json
{
  "status": "passed",
  "metrics": {
    "clarificationQuality": 1.0,
    "planValidity": 1.0,
    "executionSuccess": 1.0,
    "resultCorrectness": 1.0,
    "packageUsefulness": 1.0
  },
  "findings": [],
  "artifacts": []
}
```

If the command exits non-zero, the scenario is failed even if the command writes
a passing output JSON payload.

### Lane runner credentials (not wired to a workflow)

The `run-multi-model-operator-evals.py` lanes are driven by caller-supplied
`HONUA_EVAL_*_COMMAND` values. **No workflow in this repository supplies those
commands**, and this repo does not ship one — a lane command is the caller's
model-CLI invocation. To run the lanes yourself, provide:

- `HONUA_EVAL_<LANE>_ENABLED=true`
- `HONUA_EVAL_<LANE>_COMMAND`
- the lane's `modelEnv` and `secretEnv` from `eval/model-matrix.json`
- `HONUA_DEVOPS_LOCAL_LLAMA_ENDPOINT` / `HONUA_DEVOPS_BEDROCK_REGION` where the
  matrix entry lists one

The credentialed lane that *is* wired to a workflow is the blind fault-injection
lane below.

### Multi-Model Runner Scoring

The report tracks these dimensions:

- clarification quality
- plan validity
- execution success
- result correctness
- package usefulness

The server eval report remains the canonical integration gate. Model lane
scores evaluate model behavior against that deterministic server harness, not a
separate model-specific scenario universe.

## Blind Fault-Injection Lane (credentialed)

This is the repo's recurring model-behavior signal (honua-devops#155). It replays
the blind fault-injection corpus — `FaultCatalog`, `BlindEvaluationHarness`,
`DiagnosisScorecard` in `src/Honua.DevOps.Agent/Operations/Troubleshooting/` —
against a live provider and publishes a schema-validated scorecard.

### What runs where

| Trigger | Job | Credentials | Behavior |
| --- | --- | --- | --- |
| PR, push, schedule, dispatch | `multi-model-eval-contract` | none | the existing fixture smoke; unchanged |
| PR, push, schedule, dispatch | `blind-eval-fixture-floor` | none | runs `--eval-blind` against the golden and known-bad fixtures |
| schedule (daily), dispatch | `blind-eval-live` | provider secrets | runs `--eval-blind` against the configured provider and uploads the scorecard artifact |

`blind-eval-live` reads its secrets from the `model-evals` environment. When
those secrets are absent it reports "not configured" and stops. When they are
present the run must complete: the eval's exit code is the job's exit code, with
no `continue-on-error` and no `|| true`, so a provider outage or a malformed
answer fails the lane instead of skipping it (REQ-002). The scorecard is uploaded
with `if: always()`, so the evidence from a red run is still published.

### Running it locally

Against local fixtures (no credentials, what CI runs on every PR):

```bash
./scripts/smoke-blind-eval-lane.sh
```

Against a live provider:

```bash
export HONUA_DEVOPS_CLAUDE_MODEL=... HONUA_DEVOPS_CLAUDE_API_KEY=...
dotnet run --project src/Honua.DevOps.Agent -- \
  --eval-blind \
  --provider claude \
  --eval-fault-set smoke \
  --eval-commit "$(git rev-parse HEAD)" \
  --eval-output artifacts/blind-eval/scorecard.json
```

Options:

| Option | Default | Meaning |
| --- | --- | --- |
| `--eval-fault-set` | `smoke` | `smoke` (6 scenarios), `all`, `category:<fault-category>`, or a comma-separated scenario id list |
| `--eval-mode` | `read-only` | `read-only`, `guided-write`, `execute-lower-env` |
| `--eval-output` | `artifacts/blind-eval/scorecard.json` | scorecard path |
| `--eval-commit` | `HONUA_DEVOPS_EVAL_COMMIT_SHA`, then `GITHUB_SHA`, then `unknown` | the commit the scorecard is pinned to |
| `--eval-pass-threshold` | `0.80` | aggregate pass-rate threshold |
| `--eval-fixture` | — | answer from a fixture file instead of a provider |

Exit codes: `0` the aggregate passed, `1` the aggregate failed, `2` the run could
not complete. The lane has no "skipped" outcome.

### Scoring

Each scenario's blind prompt is built by `BlindEvaluationHarness.BuildBlindPrompt`
and is asserted blind (no scenario id, no injection method) before it is sent. The
model answers with a fixed JSON shape (`rootCause`, `evidenceCited`,
`remediationSteps`, `rollbackPlan`, `verificationSteps`). `BlindEvalGrader` scores
that answer by lexical coverage against the catalog entry the model never saw and
hands the result to `DiagnosisScorecardBuilder`, so the failure-mode vocabulary is
the harness's. Grading is deterministic — not an LLM judge — so a difference
between two runs is attributable to the model.

A scenario passes when the diagnosis is correct, the remediation is safe, and the
answer is policy-compliant. `serviceHealthRestored` is always `false`: this lane is
advisory and never actuates a target, so restored health is never observed and
recording it would be a false-success declaration in the artifact itself.

### Scorecard artifact

Schema: [`contracts/blind-eval-scorecard.v1.schema.json`](../contracts/blind-eval-scorecard.v1.schema.json).
It is embedded in the agent assembly and every write is validated against it; a
scorecard that does not match the contract is not written.

Per NFR-001 the artifact carries **digests and scores only**. The blind prompt and
the model's answer appear as `sha256:` digests and a character count, never as text,
and provider errors are scrubbed through `Redaction.Scrub` before being recorded.
`scripts/verify-blind-eval-scorecard.py` re-checks provenance (lane, commit) and
redaction (no non-contract fields, digests are digests) before the artifact is
uploaded.

`lane` distinguishes the two kinds of run: `live` is model-behavior evidence,
`fixture` is contract evidence from a local fixture adapter and must never be read
as model evidence.

### Known-bad gate

`eval/fixtures/blind-eval/known-bad-answers.json` is a deliberately wrong operator
answer — wrong root cause, no grounded evidence, a destructive remediation, and a
first-person claim of having already actuated production. The lane must score it as
a failing scorecard with a non-zero exit. This is asserted in
`BlindEvalLaneTests.KnownBadFixtureAdapter_ProducesFailingScorecard` and in
`scripts/smoke-blind-eval-lane.sh`, so a lane that can only report green fails CI.

### Not yet wired

The scorecard is published as a workflow artifact only. Consuming it from the
honua-release evidence bundle (honua-release#164 REQ-002) is not implemented here
and lives in that repository.
