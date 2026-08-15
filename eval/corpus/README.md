# Honua AI Representative Eval Corpus

A versioned, representative regression corpus for Honua's AI across two surfaces:

- **Studio** — the 8 generation capabilities (workflow, map, app, dashboard,
  report, form, analysis, query) in `honua-server/src/Honua.Ai/Features/*`.
- **DevOps** — the operator tool surface (~24 operations) in
  `honua-devops/src/Honua.DevOps.Agent/Operations/*`.

This is the permanent regression harness referenced by the AI architecture
plan (eval + example-library + gap-finder). It **complements** — does not
replace — the deterministic multi-model operator-eval lanes defined in
`eval/model-matrix.json` (those gate releases against `honua-server`'s
server-side eval report). This corpus instead measures the *generation /
reasoning quality* of the AI against a representative breadth of realistic
scenarios.

## Layout

```
eval/corpus/
  corpus.json                 # manifest: surfaces, schema, held-out policy, run policy
  scenarios/
    studio.json               # 37 Studio scenarios across 8 capabilities
    devops.json               # 24 DevOps scenarios across 12 operation areas
  run_corpus.py               # runner (zero-AI by default; --run-sample is opt-in)
  README.md
```

## Scenario schema

Each scenario is `{ id, capability|operation|operationArea, difficulty,
phrasing, prompt, expected_behavior, pass_criteria, held_out }`.

- `expected_behavior` is the canonical outcome category the prompt should drive
  toward: `generated | needs-clarification | unsupported | description |
  diagnosis | analysis | proposal | changeset`.
- `pass_criteria` is a human/LLM-judgeable success statement.
- `held_out: true` means **eval-only**: never use the scenario for tuning,
  prompt few-shot examples, or the example-library. ~20% per surface are
  held out as the regression tripwire.

## The definition uses no AI

The corpus *definition* is hand-derived from code; building or validating it
makes **zero model calls**. Only the bounded sample *run* calls a model.

```bash
# zero-AI: validate structure + print coverage
python3 run_corpus.py --validate

# list scenarios (optionally filtered)
python3 run_corpus.py --list --surface studio --difficulty happy --exclude-held-out
```

## Bounded measured sample (opt-in)

`--run-sample` runs a representative, budget-capped sample (`MAX_SAMPLE_CALLS`
= 30, no retries). It samples non-held-out scenarios only, never actuates any
DevOps action, and never touches the live demo alias.

```bash
export HONUA_STUDIO_BASE_URL=https://demo.honua.io
export HONUA_STUDIO_ADMIN_KEY="$(aws secretsmanager get-secret-value \
  --secret-id honua-demo-demo/admin-password --region us-west-2 \
  --query SecretString --output text)"
# DevOps lane requires an OpenAI-compatible Claude/Bedrock endpoint for the agent:
#   HONUA_DEVOPS_CLAUDE_MODEL, HONUA_DEVOPS_CLAUDE_API_KEY, HONUA_DEVOPS_CLAUDE_ENDPOINT
python3 run_corpus.py --run-sample --out artifacts/sample-run
```

### Studio lane

Calls the demo generation endpoints with `X-API-Key` = the raw SecretString of
`honua-demo-demo/admin-password`. The demo backend's default provider is
`bedrock` / `us.anthropic.claude-sonnet-4-5`. Note the **workflow** endpoint
wraps its result in a `{ success, data: { status, graph, ... } }` envelope;
the other generators return the result object directly.

### DevOps lane

The `honua-devops` agent's `claude` provider is an **OpenAI-compatible
`ChatClient`** (api key + endpoint); it has no native AWS Bedrock SigV4 path.
Running the agent against Bedrock therefore requires an OpenAI-compatible
gateway in front of Bedrock (LiteLLM / bedrock-access-gateway) exposed via
`HONUA_DEVOPS_CLAUDE_ENDPOINT`. The agent always runs in `plan` / `pr-first`
mode for the sample, so it only ever produces proposals / diagnoses / evidence
— it does not submit, apply, or roll back.

## Scoring

The sample records per scenario one of:

- **pass** — outcome matches `expected_behavior` and satisfies `pass_criteria`.
- **refine** — plausible but partial: e.g. asks to clarify on a happy-path
  prompt, or generates with caveats. Tracked separately from pass/fail.
- **fail** — wrong outcome, error, or fabricated/unsafe response.
```
