# Multi-Model Operator Evals

This document defines the `honua-devops#31` multi-model evaluation runner for
Honua AI operator workflows.

The runner does not invent its own scenario semantics. It consumes:

- the server-side eval report from `honua-server#734`
- scenario fixtures from the shared operator eval corpus
- model lane configuration from `eval/model-matrix.json`

## Runner Contract

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

## Lane Matrix

`eval/model-matrix.json` defines three lanes:

| Lane | Role | Release gate |
| --- | --- | --- |
| `claude` | hosted primary model | yes, when enabled |
| `codex` | hosted primary model | yes, when enabled |
| `local-llama` | portability/regression model | no |

Local Llama is deliberately not a release authority. It is tracked to catch
portability regressions and prompt overfitting, but hosted release gates remain
Claude and Codex. Its matrix entry uses the same provider id as the runtime
(`local-llama`) and records model, secret, and endpoint env vars so NIM and
other OpenAI-compatible endpoints are configured consistently.

## Lane Command Environment

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

## CI Behavior

`.github/workflows/multi-model-operator-evals.yml` runs the smoke path on pull
requests and pushes. The smoke path uses local fixtures under `eval/fixtures/`
so CI validates the contract without model credentials.

Credential-backed model lanes should run in a protected workflow or scheduled
environment that provides:

- `HONUA_EVAL_CLAUDE_ENABLED=true`
- `HONUA_EVAL_CLAUDE_COMMAND`
- `HONUA_DEVOPS_CLAUDE_MODEL`
- `HONUA_DEVOPS_CLAUDE_API_KEY`
- `HONUA_EVAL_CODEX_ENABLED=true`
- `HONUA_EVAL_CODEX_COMMAND`
- `HONUA_DEVOPS_CODEX_MODEL`
- `HONUA_DEVOPS_CODEX_API_KEY`
- optional `HONUA_EVAL_LOCAL_LLAMA_ENABLED=true`
- optional `HONUA_EVAL_LOCAL_LLAMA_COMMAND`
- optional `HONUA_DEVOPS_LOCAL_LLAMA_MODEL`
- optional `HONUA_DEVOPS_LOCAL_LLAMA_API_KEY`
- optional `HONUA_DEVOPS_LOCAL_LLAMA_ENDPOINT`

## Scoring

The report tracks these dimensions:

- clarification quality
- plan validity
- execution success
- result correctness
- package usefulness

The server eval report remains the canonical integration gate. Model lane
scores evaluate model behavior against that deterministic server harness, not a
separate model-specific scenario universe.
