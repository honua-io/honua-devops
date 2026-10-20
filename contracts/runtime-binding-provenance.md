The embedded `honua-runtime-binding.v1.schema.json` is copied byte for byte from
`honua-iac` commit `1bc09db`,
`infrastructure/terraform/contracts/runtime-binding.v1.schema.json`.
SHA-256: `64a67c6acb45acadf5ccf758b29c05134c50a2a21e9684c94e9e574a8f9cd60c`.
The contract test pins these bytes; updates must review the canonical producer
and consumer together and update this receipt and test.

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
