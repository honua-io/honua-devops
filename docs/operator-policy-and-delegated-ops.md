# Operator Policy and Delegated Operations

This document captures the baseline policy contract for `honua-devops#19`.

## Goals

- default to PR-first operator behavior
- allow direct execution only when policy explicitly allows it
- make delegated support sessions scoped, time-bound, and customer-visible
- require stronger evidence and post-action review for break-glass work
- emit an audit hook target for every write-capable plan or execution

## Approval Modes

Supported approval modes:

- `pr-first`
- `direct-allowed`
- `break-glass-only`

Default:

- `pr-first`

Semantics:

- `pr-first`: normal write execution should move through proposal and approval flow before direct execution
- `direct-allowed`: policy allows direct execution when the execution tier also allows it
- `break-glass-only`: direct execution is reserved for break-glass actions only

## Support Sessions

Delegated support sessions must declare:

- access scope
- TTL in minutes
- customer visibility

Supported access scopes:

- `disabled`
- `read-only`
- `operator-scoped`

## Break-Glass

Break-glass actions should emit stronger evidence than normal operations:

- operator justification
- incident context
- rollback intent
- post-action review requirement
- audit hook target

## Audit Hooks

Every write-capable path should declare an audit hook target, even when the current implementation is only a baseline placeholder such as `stdout-evidence`.
