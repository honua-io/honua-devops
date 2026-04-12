# Supply-Chain Baseline (MVP)

This repo now carries a baseline supply-chain policy and CI workflow scaffolding.

## Baseline Controls

1. SBOM generation in CI (`CycloneDX` output)
2. Vulnerability scan gate for `HIGH`/`CRITICAL` (Trivy filesystem scan)
3. Artifact provenance attestation (conditional on public repo or `ENABLE_GITHUB_ATTESTATIONS=true`)
4. SARIF upload to GitHub Code Scanning (conditional on public repo or `ENABLE_GITHUB_CODE_SCANNING=true`)
5. Helm chart static/provenance check hook (optional enforcement)

## Workflow

- `.github/workflows/supply-chain-baseline.yml`

Runs on:
- pull requests
- pushes to `main`
- manual dispatch

## Private Repo Considerations

GitHub artifact attestation and Code Scanning SARIF uploads require features
that are not available on all repository plans. The supply-chain workflow gates
these steps with conditional checks:

- **Attestation** (`actions/attest-build-provenance`): skipped on private repos unless `vars.ENABLE_GITHUB_ATTESTATIONS` is `true`
- **SARIF upload** (`github/codeql-action/upload-sarif`): skipped on private repos unless `vars.ENABLE_GITHUB_CODE_SCANNING` is `true`

Set these as GitHub repository variables when the features are enabled for your plan.

## Helm Provenance

`scripts/helm-provenance-check.sh` supports:
- chart lint/package checks by default
- automatic Helm dependency repo configuration (adds `bitnami` and runs `helm repo update` before building dependencies)
- auto-detecting the current Honua chart path in `honua-helm`
- strict provenance verification when:
  - `HELM_SIGNED_PACKAGE_URL` is provided
  - `HELM_PROV_URL` is provided
  - `HELM_KEYRING_PATH` is provided

If the chart layout changes again, set `HELM_CHART_PATH` explicitly.

Use strict mode in release branches:

```bash
HELM_ENFORCE_PROVENANCE=true \
HELM_SIGNED_PACKAGE_URL="https://..." \
HELM_PROV_URL="https://..." \
HELM_KEYRING_PATH="./keys/pubring.gpg" \
./scripts/helm-provenance-check.sh
```

## Launch Caveat

If strict Helm provenance inputs are missing, baseline CI runs static checks and records a warning. Keep `status/experimental` on release scope until strict provenance is enabled.
