# SLO Release Gates (MVP Baseline)

Initial SLO release gate contract for launch-phase deploy decisions.

## Gate Inputs

Required metrics:
- Availability percentage
- Error rate percentage
- P95 latency in milliseconds

## MVP Baseline Thresholds

- Availability: `>= 99.0`
- Error rate: `<= 1.0`
- P95 latency: `<= 1200ms`

## Gate Script

Use `scripts/slo-release-gate.sh` in CI/CD before promotion:

```bash
SLO_AVAILABILITY_PERCENT=99.4 \
SLO_ERROR_RATE_PERCENT=0.2 \
SLO_P95_MS=810 \
./scripts/slo-release-gate.sh
```

Or fetch metrics from a JSON endpoint:

```bash
HONUA_SLO_JSON_URL="https://metrics.example/slo/current" \
./scripts/slo-release-gate.sh
```

Accepted JSON keys (first match wins):
- availability: `availability_percent`, `availability`, `slo.availability`
- error rate: `error_rate_percent`, `error_rate`, `slo.error_rate`
- p95 latency: `p95_latency_ms`, `latency_p95_ms`, `slo.p95_latency_ms`

## Rollout Guidance

- MVP: warn + block release when any threshold is violated.
- Beta: add multi-window burn-rate checks.
- GA: include automatic rollback triggers and per-tenant SLO segmentation.
