# SLO Release Gates

Baseline SLO enforcement contract for `honua-devops#5`.

## Gate Inputs

Required metrics:
- Availability percentage
- Error rate percentage
- P95 latency in milliseconds
- Burn rate over `5m`, `30m`, and `6h` when burn-rate checks are enabled

## Thresholds

- Availability: `>= 99.0`
- Error rate: `<= 1.0`
- P95 latency: `<= 1200ms`
- Burn rate `5m`: `<= 14.4`
- Burn rate `30m`: `<= 6.0`
- Burn rate `6h`: `<= 3.0`

The gate script now also prints:

- availability error-budget remaining ratio
- error-rate budget remaining ratio
- latency headroom ratio
- overall error-budget remaining ratio

## Gate Script

Use `scripts/slo-release-gate.sh` in CI/CD before promotion:

```bash
SLO_AVAILABILITY_PERCENT=99.4 \
SLO_ERROR_RATE_PERCENT=0.2 \
SLO_P95_MS=810 \
SLO_BURN_RATE_5M=1.4 \
SLO_BURN_RATE_30M=0.9 \
SLO_BURN_RATE_6H=0.5 \
SLO_ENABLE_BURN_RATE_CHECKS=true \
./scripts/slo-release-gate.sh
```

Or fetch metrics from a JSON endpoint:

```bash
HONUA_SLO_JSON_URL="https://metrics.example/slo/current" \
SLO_ENABLE_BURN_RATE_CHECKS=true \
./scripts/slo-release-gate.sh
```

Accepted JSON keys (first match wins):
- availability: `availability_percent`, `availability`, `slo.availability`
- error rate: `error_rate_percent`, `error_rate`, `slo.error_rate`
- p95 latency: `p95_latency_ms`, `latency_p95_ms`, `slo.p95_latency_ms`
- burn rate `5m`: `burn_rate_5m`, `burn_rates["5m"]`, `slo.burn_rate_5m`
- burn rate `30m`: `burn_rate_30m`, `burn_rates["30m"]`, `slo.burn_rate_30m`
- burn rate `6h`: `burn_rate_6h`, `burn_rates["6h"]`, `slo.burn_rate_6h`

If burn-rate values are absent, the script stays backward-compatible and disables burn-rate checks unless `SLO_ENABLE_BURN_RATE_CHECKS=true`.

## Maintenance Suppression

The gate supports explicit maintenance suppression:

- `HONUA_SLO_MAINTENANCE_ACTIVE=true`
- `HONUA_SLO_MAINTENANCE_FILE=/path/to/maintenance.flag`
- `HONUA_SLO_SUPPRESS_ALERTS_DURING_MAINTENANCE=true`

When maintenance suppression is active, the script still evaluates the metrics but suppresses failures unless `HONUA_SLO_ENFORCE_DURING_MAINTENANCE=true`.

## Post-Deploy Watch

Use `scripts/slo-release-watch.sh` for canary or rollout observation windows:

```bash
HONUA_SLO_JSON_URL="https://metrics.example/slo/current" \
SLO_ENABLE_BURN_RATE_CHECKS=true \
SLO_WATCH_INTERVAL_SECONDS=60 \
SLO_WATCH_MAX_SAMPLES=10 \
SLO_WATCH_CONSECUTIVE_FAILURES=2 \
SLO_WATCH_AUTO_ROLLBACK=true \
SLO_WATCH_ROLLBACK_COMMAND="honua-gitops rollback --service roads-api --env prod --to-revision release/2026.02" \
./scripts/slo-release-watch.sh
```

Exit codes:

- `0`: watch passed
- `1`: watch failed without rollback
- `2`: watch triggered rollback

## Observability Assets

- Grafana dashboard: `observability/grafana/honua-slo-dashboard.json`
- Prometheus recording rules: `observability/prometheus/honua-slo-recording-rules.yml`
- Prometheus alert rules: `observability/prometheus/honua-slo-alert-rules.yml`
- Alertmanager route template: `observability/alertmanager/honua-slo-routes.template.yml`

The Prometheus alert rules include:

- multi-window burn-rate alerts
- availability and latency violation alerts
- durability alerts for backup success and replication lag
- runbook links in annotations
- maintenance suppression via `honua:maintenance_window:active`
- route labels for PagerDuty, Slack, and email

## CI / PR Checks

`.github/workflows/slo-enforcement-baseline.yml` runs on pull requests and pushes to `main`, giving the repo a dedicated SLO enforcement PR check. It validates:

- `scripts/slo-release-gate.sh`
- `scripts/slo-release-watch.sh`
- `scripts/smoke-slo-release-gate.sh`
- `scripts/smoke-slo-release-watch.sh`
- `scripts/validate-slo-assets.sh`

## Verification

Run the full local baseline:

```bash
./scripts/smoke-slo-release-gate.sh
./scripts/smoke-slo-release-watch.sh
./scripts/validate-slo-assets.sh
```
