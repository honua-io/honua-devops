# Install handoff system verification

`SystemInstallHandoffVerifierTests` qualifies the production verifier itself. The
test path launches an executable MCP proxy child and a live loopback candidate
server; it does not substitute `IInstallHandoffVerifier` with the provisioning
test fake.

The matrix covers the complete happy path (pinned package integrity, readiness,
authenticated structured candidate identity, initialize, two-page roster, and
Admin tool call) and these fail-closed boundaries:

| Boundary | Qualified result |
| --- | --- |
| Missing/denied secret resolution | `secret-resolution-failed` |
| Registry command unavailable | `proxy-registry-unavailable` |
| Package integrity mismatch | `proxy-integrity-mismatch` |
| HTTP readiness failure | `handoff-health-failed` |
| Admin HTTP 401/403 | `handoff-auth-failed` |
| Candidate only present as a substring | `candidate-identity-mismatch` |
| Proxy exits before replying | `handoff-verification-failed` |
| Non-JSON stdout | `mcp-stdout-noise` |
| Response without an id | `mcp-response-malformed` |
| Response for another request id | `mcp-response-out-of-order` |
| Repeated pagination cursor | `mcp-pagination-loop` |
| Required tool absent | `mcp-roster-incomplete` |
| Silent proxy / deadline expiry | `handoff-verification-timeout` |

Every process fault is bounded by the verification-wide deadline. The verifier
kills the whole process tree and awaits exit in `finally`; the tests independently
check that the recorded PID leaves `/proc`. The secret-scan verdict is earned, not
asserted: before a success is returned, the verifier serializes the exact result that
becomes evidence and scans it for the resolved admin key; a hit returns
`secret-scan-failed`, and a child that has not exited returns `proxy-not-reaped`.
The tests independently scan every serialized result for the key.

These are Linux system tests (bash child, `/proc` reap check) and run in the
hosted `agent-tests` job.

A successful receipt binds the provisioning operation, candidate, package/version
and integrity, endpoint-identity digest, identity-response digest, observed roster
and roster digest, verifier outcome, child exit/reap state, and secret-scan verdict.
No receipt or provision binding is written for any non-ready outcome.

## Not qualified here

The stand-in proxy is an executable script that speaks MCP over stdio, and the
candidate is a loopback listener. Running the manifest-pinned published npm proxy
against a real installed AWS cell needs a deployed candidate and credentials; that
leg is owned by the honua-release terminal arc (honua-release#129) and is not
claimed by these tests. No fake receipt or `verified=true` can stand in for it:
`VerifyInstallHandoffAsync` writes a binding only from a successful verifier
result, and the production host never injects a verifier, so it always runs
`SystemInstallHandoffVerifier`.
