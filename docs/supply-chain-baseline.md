# Supply-Chain Baseline (MVP)

This repo now carries a baseline supply-chain policy and CI workflow scaffolding.

## Baseline Controls

1. SBOM generation in CI (`CycloneDX` output)
2. Vulnerability scan gate for `HIGH`/`CRITICAL`
3. Artifact provenance attestation
4. Helm chart static/provenance check hook (optional enforcement)

## Workflow

- `.github/workflows/supply-chain-baseline.yml`

Runs on:
- pull requests
- pushes to `main`
- manual dispatch

## Helm Provenance

`scripts/helm-provenance-check.sh` supports:
- chart lint/package checks by default
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
