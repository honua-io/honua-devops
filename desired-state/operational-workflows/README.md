# desired-state/operational-workflows/

`OperationalWorkflow` (`honua.io/v1alpha1`) — the declarative catalog of operations the AI DevOps
operator may run. The governance layer over the executors (`RollbackExecutor`, `PromotionExecutor`,
the operator function tools): a workflow may not enter the catalog unless it is **rollback-safe**.

Each workflow declares `preconditions → steps → verify`, an **`autonomyTier` (1/2/3)**, an
`executionPolicyRef` (binds it to an `ExecutionPolicy`), and — first-class, per the desired-state
design rule that "promotion and rollback references are first-class fields" — a **`rollback`
(`procedure` + `verify`)** plus an `integrationTest` reference.

`DesiredStateValidationTests` validates every `OperationalWorkflow` and **fails closed**: a workflow
missing a non-empty `rollback.procedure` AND `rollback.verify` is rejected — you do not let an
autonomous operator run an action it cannot prove it can undo. This complements the existing
`ExecutionPolicy` `rollback-intent` required-check (intent declared) by requiring the rollback be
*specified and verifiable* per workflow.

`ops-finding-proposal` binds the AI-assisted day-2 loop to the same catalog. Its
read phase is MCP-only and bounded; its only write is the deterministic finding
id handed to the server's finding-proposal route. Honua retains the opaque
execution payload and owns executor discovery, autonomy, approval, execution,
and compensation.
