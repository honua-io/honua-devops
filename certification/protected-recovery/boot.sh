#!/usr/bin/env bash
# Boots an isolated honua-server candidate whose self-hosted rolling deploy
# backend (honua-yarp-rolling) owns a real protected activation: it launches
# workload replicas through the host's container runtime, swaps its embedded
# proxy at cutover, retains the prior replica through the post-activation
# observation window, and runs the telemetry gate against a Prometheus query
# stub whose answers are the injected fault.
#
#   boot.sh up      create network, PostGIS, Redis, telemetry stub, workload images, server
#   boot.sh restart restart only the server container (controller-crash fault)
#   boot.sh down    remove everything this script created
#
# Secrets (admin key, database password, key-ring certificate) are minted per run
# into $PROOF_WORK (outside the repository) and never written anywhere else.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="${PROOF_WORK:?set PROOF_WORK to a private directory outside the repository}"
# `up` records the image so a `restart` (the controller-crash fault) boots the same candidate.
image="${PROOF_SERVER_IMAGE:-$(cat "$work/image" 2>/dev/null || echo ghcr.io/honua-io/honua-server:nightly-d1fc139)}"
prefix="${PROOF_PREFIX:-devops191}"
port="${PROOF_SERVER_PORT:-19191}"
active_port="${PROOF_ACTIVE_PORT:-19181}"
standby_port="${PROOF_STANDBY_PORT:-19182}"
network="${prefix}-net"
pg="${prefix}-postgres"
redis="${prefix}-redis"
stub="${prefix}-telemetry"
server="${prefix}-server"
docker_cli="$(readlink -f "$(command -v docker)")"

down() {
  docker rm -f "$server" "$stub" "$redis" "$pg" >/dev/null 2>&1 || true
  docker ps -aq --filter "label=honua.target=proof-selfhosted" | xargs -r docker rm -f >/dev/null 2>&1 || true
  docker rm -f "${prefix}-app-${active_port}" "${prefix}-app-${standby_port}" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
}

wait_ready() {
  for _ in $(seq 1 60); do
    if [ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${port}/healthz/ready" || true)" = "200" ]; then
      return 0
    fi
    sleep 2
  done
  docker logs --tail 80 "$server" >&2
  return 1
}

start_server() {
  docker rm -f "$server" >/dev/null 2>&1 || true
  # The backend drives `docker run/ps/rm` itself, so the server gets the host
  # runtime's CLI and socket. Replicas publish on the host; the server reaches
  # them (health probe and proxy destination) through host.docker.internal.
  docker run -d --name "$server" --network "$network" -p "127.0.0.1:${port}:8080" \
    --user root \
    --add-host host.docker.internal:host-gateway \
    --env-file "$work/server.env" \
    -v "$work/appsettings.Production.json:/app/appsettings.Staging.json:ro" \
    -v "$work/keyring.p12:/app/keyring.p12:ro" \
    -v "${docker_cli}:/usr/local/bin/docker:ro" \
    -v /var/run/docker.sock:/var/run/docker.sock \
    "$image" >/dev/null
  wait_ready
}

up() {
  down
  mkdir -p "$work" && chmod 700 "$work"
  printf '%s' "$image" > "$work/image"
  docker network create "$network" >/dev/null

  pg_password="$(openssl rand -hex 16)"
  docker run -d --name "$pg" --network "$network" \
    -e POSTGRES_DB=honua -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD="$pg_password" \
    postgis/postgis:16-3.4 >/dev/null
  docker run -d --name "$redis" --network "$network" redis:7.4-alpine redis-server --appendonly no >/dev/null

  mkdir -p "$work/telemetry" && echo '{"*": {"mode": "empty"}}' > "$work/telemetry/state.json"
  : > "$work/telemetry/queries.jsonl"
  chmod 777 "$work/telemetry" && chmod 666 "$work/telemetry/state.json" "$work/telemetry/queries.jsonl"
  docker run -d --name "$stub" --network "$network" \
    -v "$here/telemetry-stub/stub.py:/srv/stub.py:ro" -v "$work/telemetry:/control" \
    python:3.12-alpine python /srv/stub.py >/dev/null

  for revision in prior-a candidate-b candidate-c; do
    docker build -q --build-arg WORKLOAD_REVISION="$revision" -t "${prefix}-workload:${revision}" "$here/workload" >/dev/null
  done

  for _ in $(seq 1 40); do
    if [ "$(docker logs "$pg" 2>&1 | grep -c "PostgreSQL init process complete")" -gt 0 ] &&
      docker exec "$pg" pg_isready -U postgres -d honua >/dev/null 2>&1; then
      break
    fi
    sleep 2
  done
  docker exec "$pg" psql -U postgres -d honua -q -c "CREATE EXTENSION IF NOT EXISTS postgis" -c "CREATE EXTENSION IF NOT EXISTS postgis_raster"

  extract="$(docker create "$image")"
  docker cp "${extract}:/app/appsettings.Production.json" "$work/appsettings.Production.json"
  docker rm "$extract" >/dev/null
  chmod 644 "$work/appsettings.Production.json"

  keyring_password="$(openssl rand -hex 16)"
  openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj "/CN=${prefix}-proof" \
    -keyout "$work/keyring.key" -out "$work/keyring.crt" >/dev/null 2>&1
  openssl pkcs12 -export -in "$work/keyring.crt" -inkey "$work/keyring.key" \
    -out "$work/keyring.p12" -passout "pass:${keyring_password}" >/dev/null 2>&1
  rm -f "$work/keyring.key" "$work/keyring.crt"
  chmod 644 "$work/keyring.p12"

  # Static-key OIDC issuer for tenant-bound principals (journey.py mints short-lived HS256 JWTs).
  issuer_key="$(openssl rand -hex 32)"
  printf '%s' "$issuer_key" > "$work/issuer-key" && chmod 600 "$work/issuer-key"

  admin_key="Pr00f-$(openssl rand -hex 12)!"
  printf '%s' "$admin_key" > "$work/admin-key" && chmod 600 "$work/admin-key"

  cat > "$work/server.env" <<ENV
ASPNETCORE_ENVIRONMENT=Staging
ASPNETCORE_URLS=http://+:8080
PUBLIC_BASE_URL=http://127.0.0.1:${port}
HONUA_ADMIN_PASSWORD=${admin_key}
ConnectionStrings__DefaultConnection=Server=${pg};Port=5432;Database=honua;User Id=postgres;Password=${pg_password};
ConnectionStrings__honua=Server=${pg};Port=5432;Database=honua;User Id=postgres;Password=${pg_password};
ConnectionStrings__Redis=${redis}:6379
Security__ConnectionEncryption__MasterKey=$(openssl rand -hex 32)
Security__ConnectionEncryption__Salt=$(openssl rand -base64 32)
HostValidation__AllowedHosts__0=localhost
HostValidation__AllowedHosts__1=127.0.0.1
Licensing__DevGrantEdition=Pro
Operations__SecretChannel__KeyRingCertificatePath=/app/keyring.p12
Operations__SecretChannel__KeyRingCertificatePassword=${keyring_password}
Operations__Policy__Rules__0__OperationId=control-plane.deploy.rollback
Operations__Policy__Rules__0__Decision=Allow
Operations__Policy__Rules__0__Reason=Deploy rollback is admitted to the recovery fence on this proof target.
Oidc__Enabled=true
Oidc__RequireHttps=true
Oidc__Generic__Enabled=true
Oidc__Generic__Authority=https://issuer.protected-recovery-proof.invalid
Oidc__Generic__ClientId=honua-protected-recovery-proof
Oidc__Generic__ClientSecret=unused-static-key-issuer
Oidc__TokenValidation__SymmetricSigningKey=${issuer_key}
Oidc__TokenValidation__ValidIssuers__0=https://issuer.protected-recovery-proof.invalid
Oidc__TokenValidation__ValidAudiences__0=honua-protected-recovery-proof
Oidc__TokenValidation__ClockSkew=00:00:00
ControlPlane__SelfHosted__Enabled=true
ControlPlane__SelfHosted__Host=host.docker.internal
ControlPlane__SelfHosted__ActivePort=${active_port}
ControlPlane__SelfHosted__StandbyPort=${standby_port}
ControlPlane__SelfHosted__ContainerNamePrefix=${prefix}-app
ControlPlane__SelfHosted__RoutePath=/${prefix}-proxy/{**catch-all}
ControlPlane__SelfHosted__DrainDelaySeconds=2
ControlPlane__TelemetryConnections__0__ConnectionId=proof-prometheus
ControlPlane__TelemetryConnections__0__Provider=prometheus
ControlPlane__TelemetryConnections__0__BaseUrl=http://${stub}:9090
ControlPlane__TelemetryConnections__0__AllowPrivateNetworks=true
ControlPlane__DeployTargets__0__TargetId=proof-selfhosted
ControlPlane__DeployTargets__0__TargetKind=SelfHostedRolling
ControlPlane__DeployTargets__0__Backend=honua-yarp-rolling
ControlPlane__DeployTargets__0__Environment=proof
ControlPlane__DeployTargets__0__TargetName=proof-selfhosted
ENV
  chmod 600 "$work/server.env"
  start_server
  echo "server ready on http://127.0.0.1:${port}"
}

case "${1:-}" in
  up) up ;;
  restart) start_server ;;
  down) down ;;
  *) echo "usage: $0 up|restart|down" >&2; exit 2 ;;
esac
