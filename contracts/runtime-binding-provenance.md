The canonical `honua-runtime-binding.v1.schema.json` is now owned by
`honua-devops` at `contracts/honua-runtime-binding.v1.schema.json` (introduced
in commit `b9695ccc229b6c925603c9bc2f349a6ddcb838cb`). SHA-256:
`638682e765247de4d5d1918729c9172bf9d23af2b0c684e357b21a35ad300604`.
The contract test pins these bytes. Future changes must update the schema,
consumer, receipt, and digest together.

The original embedded schema was copied from `honua-iac` commit `1bc09db`,
`infrastructure/terraform/contracts/runtime-binding.v1.schema.json`, with
SHA-256 `64a67c6acb45acadf5ccf758b29c05134c50a2a21e9684c94e9e574a8f9cd60c`.
That is historical provenance, not the current canonical identity.

Terraform facts describe declared resource identity. They are not live provider
observations. Lambda handoff v2 additionally requires current canonical registered
resource authority, fixed read-only AWS observations of the serving version and
route, and the existing public Honua health/auth/MCP probes. Missing new server
receipt fields fail closed. Explicit `tenantId: null` represents unbound tenancy;
it does not imply a public tenant. Provider outputs and credentials are never
written into the observation evidence wholesale.

Native EKS runtime facts remain a schema contract, not a qualified private install
handoff: cluster-only Terraform outputs cannot prove a real chart installation,
immutable serving workload, or public route. No Argo or Flux is required.
