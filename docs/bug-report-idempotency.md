# Bug-report event idempotency

The `--bugreport-listen` adapter persists terminally handled
`ticket.bug_report.v1` event IDs so a sender retry cannot file a second issue
after an operator restart. This store complements the destination-repository
duplicate search; it does not replace signature verification, timestamp
freshness checks, or fingerprint-based issue deduplication.

## Configuration

| Variable | Default | Meaning |
| --- | --- | --- |
| `HONUA_DEVOPS_BUGREPORT_IDEMPOTENCY_STORE` | `file` | `file` for durable local state; `memory` for the explicit non-durable emergency fallback. |
| `HONUA_DEVOPS_BUGREPORT_IDEMPOTENCY_PATH` | per-user local application data under `honua-devops/bug-report-event-ids.json` | JSON state file. Relative configured paths are resolved from the process working directory. |
| `HONUA_DEVOPS_BUGREPORT_IDEMPOTENCY_RETENTION_SECONDS` | `86400` | Retention for handled IDs. It must be at least the webhook replay window. |
| `HONUA_DEVOPS_BUGREPORT_IDEMPOTENCY_MAX_ENTRIES` | `100000` | Hard capacity; oldest entries are removed first. |

For a container or orchestrated service, set an explicit path under a mounted
persistent volume. One file can be shared by cooperating local processes: a
sidecar lock serializes read/compact/write operations, and updates replace the
JSON file atomically. A network filesystem must provide normal exclusive-create,
file-sharing, and atomic rename semantics; otherwise use one writer per volume.

Expired entries are compacted during access. Capacity eviction is deterministic
(oldest timestamp, then event ID) and is a final bound, not the normal cleanup
path. The state contains only event IDs and processing timestamps—no customer
payload, issue body, secret, or telemetry.

## Failure and fallback semantics

Durable mode never silently falls back to memory. An unreadable directory,
lock timeout, corrupt JSON document, unsupported state version, or failed atomic
write fails the operation. On startup this prevents the listener from binding;
during handling it causes the delivery to fail and be retried. This fail-closed
posture preserves the existing GitHub duplicate search as the recovery backstop
without pretending a claim was persisted.

Set the backend to `memory` only as an explicit emergency action when the durable
volume cannot be restored. Startup writes a warning because restart protection is
then absent. The in-memory store retains the same TTL and capacity bounds.

## Migration and recovery

The prior in-memory backend has no durable data to import. The first start on
this version creates an empty file during listener startup; destination-repo
duplicate search remains the backstop for events handled before migration.

To move the state file:

1. Stop every listener using the file.
2. Copy the JSON file to the new persistent location without editing it.
3. Update `HONUA_DEVOPS_BUGREPORT_IDEMPOTENCY_PATH` and restart one listener.
4. Confirm a known retained event is rejected as `duplicate-event` before
   bringing up additional listeners.

If the file is corrupt, stop the listener and preserve the original as evidence.
Restore a known-good backup or deliberately move the corrupt file aside and
restart with an empty store. Moving it aside weakens replay protection for the
remaining retention window, so rely on the GitHub duplicate search and record
the operator action.
