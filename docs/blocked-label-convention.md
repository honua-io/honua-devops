# Blocked-Label Convention

`state/blocked` is a machine-verifiable claim, not a mood. This document defines
what makes the label valid and how it is re-verified.

Cross-refs: **honua-devops#167** (execution-state hygiene sweeper).

- Sweeper: [`scripts/sweep-blocked-labels.py`](../scripts/sweep-blocked-labels.py)
- Self-test: [`scripts/smoke-blocked-label-sweep.sh`](../scripts/smoke-blocked-label-sweep.sh)
- CI: [`.github/workflows/blocked-label-sweep.yml`](../.github/workflows/blocked-label-sweep.yml)

## The rule

> An issue may carry `state/blocked` **only** while its body or one of its
> comments cites at least one blocker by reference, and at least one cited
> blocker is still open.

Two failure modes follow directly, and both are what the sweeper looks for:

- **UNCITED** — the label is present but no blocker reference can be parsed.
  Nothing can verify the claim, so nobody can ever retire it except by hand.
- **STALE** — every cited blocker has closed (or merged). The label now
  misreports the work as unstartable; it should become `state/ready`.

## Citation grammar

A citation is a **marker** followed by one or more **references**. The references
may sit on the marker's own line, or — when the marker line carries none, as with
a `## Dependencies` heading — on the lines that follow it, blank lines included.
A markdown heading always ends the run, so a marker never reaches into the next
section.

### Markers

Case-insensitive, `-` or whitespace tolerated inside the phrase:

`Blocked by` · `Blocked on` · `Depends on` · `Depends` · `Depends upon` ·
`Dependency` · `Dependencies` · `Blocker` · `Blockers`

### References

| Form | Example | Resolves to |
| --- | --- | --- |
| Full URL | `https://github.com/honua-io/honua-server/issues/3475` | as written |
| Qualified | `honua-io/honua-server#3475` | as written |
| Org shorthand | `server#3475`, `sdk-js#1397`, `studio#40` | `honua-io/<repo>` via the alias table |
| Bare | `#30` | the same repository as the labelled issue |

Org shorthand is included because it is what the existing corpus actually
writes. The alias table maps both the full repository name and its
`honua-`-stripped short form (`honua-sdk-js` and `sdk-js`) to
`honua-io/honua-sdk-js`. It is extensible without a code change via
`--repo-alias short=owner/repo` or `HONUA_SWEEP_REPO_ALIASES`. A shorthand that
resolves to nothing is reported as an unresolved reference, never silently
dropped.

Pull requests are valid blockers. A merged or closed PR counts as closed.

### Direction matters

Scanning of a marker's reference run **stops** at a reverse-direction word —
`Blocks`, `Blocking`, `Supersedes`, `Closes`, `Fixes`, `Resolves` — when that
word actually introduces something, i.e. it is followed by `:`, a dash, or a
reference. So the very common one-liner

```text
Depends: studio#40, server#3303, server#3312. Blocks: server#3305 and the compose+save receipt.
```

contributes three blockers and zero false ones, while

```text
- #40 (live agent loop) — blocking for the live lane; corpus authoring can start now
```

is read as prose and still contributes `#40`.

### Bare references are ambiguous — qualify them

A bare `#N` resolves against the repository the label sits on. Writing
`honua-server#3430 ... and #3431` inside a honua-devops issue therefore cites
`honua-devops#3431`, which does not exist. The sweeper reports the reference as
unresolved rather than guessing; qualify the second reference.

### Valid

```markdown
Blocked by honua-server#3475, which supplies the per-source observation time.
```

```markdown
## Depends / blocks
Depends on: https://github.com/honua-io/honua-sdk-js/issues/1330 (S-A publish)  ·  Blocks: honua-studio#41
```

```markdown
Depends on #30, sdk-js#1397, server#3412, and server#3303.
```

### Not valid

```markdown
Blocked on the platform team's capacity.
```

```markdown
Waiting for the SDK release.
```

Neither names a reference, so neither can be re-verified. Both are UNCITED.

## Classification

Per open issue carrying the label:

| Class | Condition | Suggested action |
| --- | --- | --- |
| `STALE` | at least one citation, all resolvable, **all** closed/merged | swap `state/blocked` -> `state/ready`, comment naming the closed blockers |
| `UNCITED` | no parseable citation in body or comments | comment asking for a blocker citation, or drop the label |
| `OK` | at least one cited blocker still open | none |
| `ERROR` | the issue's own data could not be read | none; re-run |

**Unresolved references are never treated as closed.** If a citation cannot be
resolved — unknown alias, 404, no token scope for a private repo, a rate limit
that survived the retries — the issue cannot be `STALE`; it is reported as `OK`
with the unresolved reference named. The sweeper only ever argues *for* keeping
a blocked label on incomplete evidence, never against.

A malformed, empty, or absent body is not an error. It is UNCITED.

## Verification posture

- The sweep runs **dry-run by default** and mutates nothing. It prints a
  markdown report and exits 0 whether or not it found anything.
- `--enforce` exists as a guard only. It refuses unless `ENFORCE_SWEEP=true` is
  also set, and even then this slice has no mutation path and exits non-zero.
  The comment-and-flip body lands in a later slice of honua-devops#167.
- When enforcement does land, every comment and label change must be
  attributable to the bot identity that made it — the workflow's token
  identity, named in the comment it writes. Sweep actions are never authored as
  a human.

## Running it

```bash
# Both default repos, dry run, report to stdout
python3 scripts/sweep-blocked-labels.py

# A different target set, report to a file, machine-readable copy alongside
python3 scripts/sweep-blocked-labels.py \
  --repo honua-io/honua-server --repo honua-io/honua-console \
  --output artifacts/blocked-sweep/report.md \
  --json-out artifacts/blocked-sweep/report.json

# Parser self-test — no network, no gh
python3 scripts/sweep-blocked-labels.py --self-test
```

Cross-repo reads need a token with `repo` scope on every target. In CI that is
`SWEEPER_GH_TOKEN`; without it the workflow falls back to `github.token` and
sweeps only this repository.
