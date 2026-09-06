# Federated operation lineage

This implements the DevOps evidence-retention and deploy-control federation slice
of #150. The release promise is reconstructing the exact provisioning plan,
approval, apply, verified handoff, and subsequent server operation from durable
receipts, without using log summaries or substituting local IDs.

## Identity and authority

`auditEventId` identifies one tool response/audit emission. A replay gets a new
audit event but retains the original `applyAuditEventId`. Neither is a runtime
operation ID. JSONL is a searchable, non-authoritative diagnostic replica.
Deleting it does not delete the provisioning identity or evidence.

Pass a stable `idempotencyKey` to `provision_infrastructure` when planning. DevOps
reserves its provisioning ID on disk before starting the plan. Concurrent calls
and process restarts recover the same response; a changed request conflicts. A
crash before the result is recorded returns `plan-indeterminate`, never a second
plan. An empty key explicitly starts new work. Apply continues to use the saved
plan's one-use claim and upstream approval. Retrying a completed apply with the
same approval returns the original evidence without invoking the substrate.

Provisioning state and immutable evidence live in the current user's local
application-data directory, under `honua-devops/provisioning`. On Linux this is
normally `~/.local/share/honua-devops/provisioning`. Preserve this directory across
operator restarts/container replacement. Evidence files are created owner-only,
flushed, and atomically installed. These files are provisioning evidence, not a
second server operation, proposal, or approval store.

## Handoff to server deploy-control

The generated `honua.env.example` carries
`HONUA_DEVOPS_HONUA_API_BASE_URL` and
`HONUA_DEVOPS_ROOT_PROVISIONING_OPERATION_ID`. After verification, load these
settings for the DevOps process that creates subsequent deploy operations.
The gateway validates the retained verification/binding bytes and endpoint,
then places the root in the existing deploy request's `parameters`. The server
owns the persisted operation and idempotency behavior. Submit/rollback read and
validate the root before issuing the mutation when federation is configured.

Create, read, submit and rollback responses preserve the server's `operationId`,
`operationInstanceId`, `proposalId`, `auditId`, `correlationId`, `executionId`,
`jobId`, `providerOperationId`, decision audit and evidence references separately.
For the typed admin runtime, `operationId` is a descriptor and
`operationInstanceId` is the invocation; the client does not alias them. The
Console proposal projection no longer calls a local idempotency key or a deploy
operation ID a `proposalId`. Missing server IDs remain null.

A missing/mismatched root or operation identity, duplicate identity field, missing
retained bytes, or digest mismatch fails the lineage claim. A failed response
capture after creation may mean an operation already exists: reconcile using the
same idempotency key; do not issue a new key or infer a successful join.

## Machine-readable evidence

CLI/function/MCP responses serialize `provisioningLineage.evidenceRefs` and
`serverOperations`. JSONL copies the same fields even when its input has already
crossed JSON serialization. No summary parsing is required.

Each evidence reference has `kind`, `reference`, `sha256`, and `byteLength`.
`urn:sha256:<digest>` resolves to `provisioning/evidence/<digest>` in the protected
store. The digest covers the exact retained bytes, including whitespace. Server
receipt digests cover the HTTP entity bytes, not reserialized JSON or a truncated
preview. Resolve these references from the operator evidence volume, not by
fetching arbitrary URLs.

The verified server lineage carries plan, plan-metadata, approval, apply,
handoff, verification-evidence, verification-receipt and provision-binding
references. `handoffVerificationReceiptSha256` hashes the verification **evidence
body**; the `verification-receipt` reference independently hashes the containing
receipt file. This avoids circular self-hashes. The validator also checks IDs
inside the approval/handoff/verification and the plan hashes in metadata/apply;
valid hashes for substituted documents are insufficient.

The release-owned verifier can retain these references in its candidate receipt
and independently resolve, hash and compare the named bytes. A candidate receipt
reference/digest is absent until that authority actually produces one. Sensitive
Terraform plans and signed approvals must remain in the protected evidence
volume; publish only the references and an appropriately restricted evidence
bundle.

## Verification and remaining acceptance work

The local regression fixture crosses bootstrap, signed approval, typed apply,
handoff, verification, HTTP deploy creation/read/retry, wire serialization and
JSONL. A separate test boots the actual MCP stdio host and checks the returned
server IDs, independently hashed HTTP bytes, and matching JSONL event. Its
provisioning process runner and HTTP server are test doubles; it is **not** live AWS,
real-server persistence, OAuth, or exact-candidate certification. It asserts
independently specified IDs and exact bytes, a published SHA-256 test vector,
restart/replay identity, concurrent single claim, mismatched/missing/substituted
joins, duplicate fields, and filesystem export failure recovery.

The checked-in honua-iac document shapes originated from its offline wrappers.
Their plan hashes were inconsistent with the fake runner's explicit bytes. The
fixture now uses SHA-256 of UTF-8 `fake saved terraform plan`:
`3a691c59f9c8be1eb1ce3e7642643a7aedfa7a0314c8d01750546ea83efca6fe`.
The metadata digest was independently recomputed with Python `hashlib.sha256`
over `json.dumps(document_without_plan_metadata_digest, sort_keys=True,
separators=(',', ':')).encode('utf-8')`, matching the IaC canonicalization rule.
The resulting metadata/approval binding digest is
`058307fcb139d9c5c6eb4ed483bc244778972f12e5bf13e351f5e38d9113f4f2`.

Outstanding #150 acceptance work:

- Server trunk inspected at `2ee4eb4eca` has no dedicated
  `rootProvisioningOperationId` on the canonical admin operation/proposal
  envelope. Persisted deploy parameters provide a bounded correlation join;
  they are not an independently attested root on every server mutation.
  Canonical admin/MCP, metadata-release and finding/proposal propagation still
  require server-owned integration. This is **not released** or proven here.
- Live bootstrap → server approval/replay and OAuth actor/tenant/scope proof
  remain unproven by these test doubles. No live qualification claim is made.
- The final release#129 candidate receipt/digest and exact-candidate end-to-end
  reconstruction cannot be produced before the candidate exists. That criterion
  is released to candidate qualification for this PR; #150 remains open for the
  other incomplete integration criteria.
