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
check that the recorded PID leaves `/proc`. Results and assertions are serialized
and scanned for the resolved admin key.

A successful receipt binds the provisioning operation, candidate, package/version
and integrity, endpoint-identity digest, identity-response digest, observed roster
and roster digest, verifier outcome, child exit/reap state, and secret-scan verdict.
No receipt or provision binding is written for any non-ready outcome.
