#!/usr/bin/env python3
"""Live protected-recovery journey against a booted honua-server candidate.

Drives the server's real deploy control surface (create -> submit -> promote ->
post-activation observation window) on the self-hosted rolling backend that
boot.sh configures, injects one fault per cell, and records every request, every
response and every protection-phase transition into a JSON receipt.

The driver never decides an outcome for the server: rollbacks in the fault cells
are triggered by the server's own reconciler from the telemetry stub's answers,
and each cell asserts only what the server and the replicas report.

Usage:
  journey.py --work DIR --out receipt.json [--cells a,b,...]

Nothing secret is recorded: the admin key travels only as a request header and
headers are not captured.
"""
import argparse
import base64
import datetime as dt
import hashlib
import hmac
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request

TARGET = "proof-selfhosted"
PREFIX = "devops191"
ACTIVE_PORT = 19181
STANDBY_PORT = 19182

HEALTHY = {
    "proof_error_rate": {"mode": "value", "value": "0.001"},
    "proof_latency_p95": {"mode": "value", "value": "40"},
    "proof_samples": {"mode": "value", "value": "500"},
}

TELEMETRY_PARAMETERS = {
    "telemetry.connection": "proof-prometheus",
    "telemetry.error_rate.query": "proof_error_rate",
    "telemetry.error_rate.threshold": "0.05",
    "telemetry.latency_p95.query": "proof_latency_p95",
    "telemetry.latency_p95.threshold_ms": "500",
    "telemetry.sample_count.query": "proof_samples",
    "telemetry.sample_count.minimum": "10",
    "telemetry.warmup_seconds": "5",
    "telemetry.evidence_grace_seconds": "20",
    "telemetry.max_staleness_seconds": "60",
    "telemetry.rollback.consecutive_breaches": "1",
    "deployment.protection.observation_window_seconds": "900",
}


class Failure(Exception):
    pass


class Journey:
    def __init__(self, base, key, work):
        self.base = base.rstrip("/")
        self.key = key
        self.work = work
        self.cell = None

    # ---- evidence ----------------------------------------------------------------------

    def note(self, kind, **fields):
        entry = {"at": now(), "kind": kind, **fields}
        self.cell["events"].append(entry)
        return entry

    def http(self, method, path, body=None, record=True, principal=None):
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(self.base + path, data=data, method=method)
        if principal is None:
            request.add_header("X-API-Key", self.key)
        else:
            request.add_header("Authorization", f"Bearer {self.token(**principal)}")
        if data is not None:
            request.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                status, raw = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, raw = error.code, error.read()
        try:
            parsed = json.loads(raw) if raw else None
        except ValueError:
            parsed = raw.decode(errors="replace")
        if record:
            caller = "admin-api-key" if principal is None else principal
            self.note("http", caller=caller, request={"method": method, "path": path, "body": body},
                      response={"status": status, "body": parsed})
        return status, parsed

    def token(self, subject, tenant, roles):
        """Short-lived HS256 JWT from the proof's static-key issuer (the key never leaves $PROOF_WORK)."""
        with open(f"{self.work}/issuer-key", encoding="utf-8") as handle:
            key = handle.read().strip()
        issued = int(time.time())
        claims = {"iss": "https://issuer.protected-recovery-proof.invalid", "aud": "honua-protected-recovery-proof",
                  "sub": subject, "name": subject, "roles": roles, "iat": issued, "nbf": issued, "exp": issued + 900,
                  "jti": f"{subject}-{issued}-{time.monotonic_ns()}", "tenant_id": tenant}
        unsigned = f"{b64url(json.dumps({'alg': 'HS256', 'typ': 'JWT'}).encode())}.{b64url(json.dumps(claims).encode())}"
        return f"{unsigned}.{b64url(hmac.new(key.encode(), unsigned.encode(), hashlib.sha256).digest())}"

    def check(self, condition, message):
        self.cell["assertions"].append({"assertion": message, "held": bool(condition)})
        if not condition:
            raise Failure(message)

    # ---- environment -------------------------------------------------------------------

    def telemetry(self, state, label):
        with open(f"{self.work}/telemetry/state.json", "w", encoding="utf-8") as handle:
            json.dump(state, handle)
        self.note("telemetry-injected", label=label, answers=state)

    def image(self, revision):
        return docker("image", "inspect", "-f", "{{.Id}}", f"{PREFIX}-workload:{revision}")

    def replicas(self):
        names = docker("ps", "-a", "--filter", f"label=honua.target={TARGET}", "--format", "{{.Names}}").split()
        result = []
        for name in names:
            info = json.loads(docker("inspect", name))[0]
            labels = info["Config"]["Labels"]
            result.append({
                "name": name,
                "running": info["State"]["Running"],
                "role": labels.get("honua.role"),
                "revision": labels.get("honua.revision"),
                "workload": labels.get("io.honua.devops.proof.workload"),
            })
        return sorted(result, key=lambda replica: replica["name"])

    def served(self):
        """What the embedded proxy actually serves: real traffic through the server."""
        try:
            with urllib.request.urlopen(f"{self.base}/{PREFIX}-proxy/journey-probe", timeout=10) as response:
                return json.loads(response.read()).get("revision")
        except (urllib.error.URLError, ValueError, TimeoutError) as error:
            return f"unreachable: {error}"

    def snapshot(self, label):
        return self.note("replicas", label=label, replicas=self.replicas(), proxyServes=self.served())

    def reset(self):
        """Prior revision A serving as the active replica; nothing else for the target."""
        stale = docker("ps", "-aq", "--filter", f"label=honua.target={TARGET}").split()
        if stale:
            docker("rm", "-f", *stale)
        docker("rm", "-f", f"{PREFIX}-app-{ACTIVE_PORT}", f"{PREFIX}-app-{STANDBY_PORT}", check=False)
        prior = self.image("prior-a")
        docker("run", "-d", "--name", f"{PREFIX}-app-{ACTIVE_PORT}", "-p", f"{ACTIVE_PORT}:8080",
               "--label", f"honua.target={TARGET}", "--label", "honua.role=active",
               "--label", f"honua.revision={prior}", "--label", "io.honua.devops.proof.workload=prior-a", prior)
        # The embedded proxy destination is process state: a committed candidate leaves it on
        # the standby port, and only a restart re-derives it from the running replicas.
        time.sleep(2)
        if self.served() != "prior-a":
            self.note("environment-reset", label="server restarted so the proxy re-derives the active replica")
            restart_server(self.work)
        self.wait(lambda: self.served() == "prior-a", 60, "prior revision serves through the proxy")

    # ---- operation lifecycle -----------------------------------------------------------

    def create(self, candidate_revision, key, extra=None, drop=(), principal=None):
        parameters = dict(TELEMETRY_PARAMETERS)
        parameters.update(extra or {})
        for name in drop:
            parameters.pop(name, None)
        body = {
            "targetId": TARGET,
            "desiredRevision": self.image(candidate_revision),
            "currentRevision": self.image("prior-a"),
            "reason": f"protected recovery proof: {self.cell['name']}",
            "idempotencyKey": key,
            "correlationId": key,
            "submitImmediately": True,
            "parameters": parameters,
        }
        status, created = self.http("POST", "/api/v1/admin/deploy/operations", body, principal=principal)
        self.check(status in (200, 201) and created and created.get("operationId"), "deploy operation created and submitted")
        return created["operationId"]

    def operation(self, operation_id, record=False):
        status, body = self.http("GET", f"/api/v1/admin/deploy/operations/{operation_id}", record=record)
        if status != 200:
            raise Failure(f"GET operation {operation_id} returned {status}")
        return body

    def watch(self, operation_id, predicate, timeout, label):
        """Polls the operation, recording every distinct (status, phase, reasonCode) it passes."""
        deadline = time.time() + timeout
        last = None
        while True:
            op = self.operation(operation_id)
            protection = op.get("protection") or {}
            # The countdown digits change every cycle; the message itself is evidence.
            message = "".join(ch for ch in (op.get("currentPhase") or "") if not ch.isdigit())
            observed = (op.get("status"), protection.get("phase"), protection.get("reasonCode"), message)
            if observed != last:
                self.note("transition", operation=operation_id, status=observed[0], protectionPhase=observed[1],
                          reasonCode=observed[2], currentPhase=op.get("currentPhase"), protection=op.get("protection"))
                last = observed
            if predicate(op):
                self.note("reached", label=label, operation=op)
                return op
            if time.time() > deadline:
                self.note("timeout", label=label, operation=op)
                raise Failure(f"timed out waiting for: {label}")
            time.sleep(2)

    def wait(self, predicate, timeout, label):
        deadline = time.time() + timeout
        while not predicate():
            if time.time() > deadline:
                raise Failure(f"timed out waiting for: {label}")
            time.sleep(1)

    def activate(self, candidate_revision, key, extra=None, drop=(), principal=None):
        self.reset()
        self.telemetry(HEALTHY, "healthy evidence while the candidate activates")
        self.snapshot("before activation")
        operation_id = self.create(candidate_revision, key, extra, drop, principal)
        op = self.watch(operation_id, lambda o: phase(o) == "observing", 180,
                        "candidate promoted and observation window open")
        protection = op["protection"]
        self.check(protection["candidateRevision"] == self.image(candidate_revision), "window observes the candidate revision")
        self.check(protection["previousRevision"] == self.image("prior-a"), "window retains the prior revision")
        self.check(str(protection.get("grantId", "")).startswith("grant-"), "server sealed a recovery grant at exposure")
        self.check(protection.get("permittedCompensation") == "restore-previous-revision", "grant permits only restore-previous-revision")
        self.check(bool(protection.get("actor")), "grant is bound to the requesting actor")
        self.wait(lambda: self.served() == candidate_revision, 60, "candidate serves through the proxy")
        self.snapshot("candidate exposed, prior retained")
        return operation_id, op

    def fence(self, op, **overrides):
        protection = op["protection"]
        body = {
            "reason": f"declared recovery: {self.cell['name']}",
            "targetId": op["target"]["targetId"],
            "expectedCandidateRevision": protection["candidateRevision"],
            "expectedPreviousRevision": protection["previousRevision"],
            "expectedProtectionPhase": protection["phase"],
            "grantId": protection["grantId"],
            "policyDigest": protection["policyDigest"],
            "actor": protection["actor"],
            "notAfter": iso(dt.datetime.now(dt.timezone.utc) + dt.timedelta(minutes=10)),
            "compensation": protection["permittedCompensation"],
        }
        if protection.get("tenantId"):
            body["tenantId"] = protection["tenantId"]
        for name, value in overrides.items():
            if value is None:
                body.pop(name, None)
            else:
                body[name] = value
        return body

    def refused(self, operation_id, body, expected_status, expected_code, label, principal=None):
        before = self.operation(operation_id)
        status, response = self.http("POST", f"/api/v1/admin/deploy/operations/{operation_id}/rollback", body, principal=principal)
        after = self.operation(operation_id)
        code = response.get("code") if isinstance(response, dict) else None
        self.note("fence-refusal", label=label, expectedStatus=expected_status, expectedCode=expected_code,
                  observedStatus=status, observedCode=code)
        self.check(status == expected_status and code == expected_code,
                   f"{label}: refused {expected_status} {expected_code} (observed {status} {code})")
        # updatedAt advances on every reconcile cycle (the window countdown), so the transition
        # check compares what a recovery would change: status, completion, the protection
        # record and the recorded reason.
        self.check((before["status"], before.get("completedAt"), before.get("protection"), before.get("reason"))
                   == (after["status"], after.get("completedAt"), after.get("protection"), after.get("reason")),
                   f"{label}: operation did not transition")

    def restored(self, operation_id, label):
        op = self.watch(operation_id, lambda o: o.get("status") == "RolledBack", 180, label)
        # The reconciler's own recovery clears the window. A declared (out-of-band) recovery on
        # nightly-2cc2213 settles RolledBack with the window RETAINED, and the retained phase is
        # racy: `recovering`/`rollback-requested-out-of-band` or a stale `observing`. Recorded
        # as a server finding; the settled status is the authority and must never read unavailable.
        self.note("settled-protection", protection=op.get("protection"))
        self.check(phase(op) != "unavailable", f"settled RolledBack does not read as unavailable (phase {phase(op)})")
        self.wait(lambda: self.served() == "prior-a", 60, "prior revision serves through the proxy again")
        self.wait(lambda: not any(r["role"] == "standby" and r["running"] for r in self.replicas()), 60,
                  "failed candidate replica retired")
        self.snapshot("after recovery")
        self.check(self.served() == "prior-a", "proxy serves the prior revision after recovery")
        return op

    # ---- cells ---------------------------------------------------------------------------

    def cell_fenced_recovery(self):
        operation_id, op = self.activate("candidate-b", unique("fenced"))
        negatives = [
            ("foreign target", {"targetId": "some-other-target"}, 409, "recovery_fence_target_mismatch"),
            ("wrong candidate revision", {"expectedCandidateRevision": self.image("candidate-c")}, 409, "recovery_fence_candidate_revision_mismatch"),
            ("wrong previous revision", {"expectedPreviousRevision": self.image("candidate-c")}, 409, "recovery_fence_previous_revision_mismatch"),
            ("unknown grant", {"grantId": "grant-00000000000000000000000000000000"}, 409, "recovery_fence_grant_mismatch"),
            ("mismatched policy digest", {"policyDigest": "0" * 64}, 409, "recovery_fence_policy_digest_mismatch"),
            ("wrong protection phase", {"expectedProtectionPhase": "recovering"}, 409, "recovery_fence_protection_phase_mismatch"),
            ("unrecognized protection phase", {"expectedProtectionPhase": "made-up"}, 400, "recovery_fence_protection_phase_unrecognized"),
            ("foreign actor", {"actor": "someone-else@example.invalid"}, 403, "recovery_fence_actor_mismatch"),
            ("foreign tenant", {"tenantId": "tenant-not-mine"}, 403, "recovery_fence_tenant_mismatch"),
            ("broadened compensation", {"compensation": "delete-everything"}, 403, "recovery_fence_compensation_not_permitted"),
            ("expired grant", {"notAfter": "2000-01-01T00:00:00Z"}, 412, "recovery_fence_expired"),
            ("unknown property", {"expectedCurrentRevision": op["protection"]["previousRevision"]}, 400, "recovery_fence_unknown_property"),
        ]
        for label, overrides, status, code in negatives:
            self.refused(operation_id, self.fence(op, **overrides), status, code, label)

        # A grant that was valid when quoted and expired by the time it arrived.
        short = self.fence(op, notAfter=iso(dt.datetime.now(dt.timezone.utc) + dt.timedelta(seconds=3)))
        time.sleep(5)
        self.refused(operation_id, short, 412, "recovery_fence_expired", "grant expired in flight")

        self.snapshot("after every refusal: candidate still serving")
        self.check(self.served() == "candidate-b", "no refused recovery moved traffic")

        current = self.operation(operation_id, record=True)
        status, response = self.http("POST", f"/api/v1/admin/deploy/operations/{operation_id}/rollback", self.fence(current))
        self.check(status == 200, f"satisfied fence admitted (observed {status})")
        self.restored(operation_id, "declared recovery settled RolledBack")

        status, response = self.http("POST", f"/api/v1/admin/deploy/operations/{operation_id}/rollback", self.fence(current))
        self.check(status != 200 or (isinstance(response, dict) and response.get("status") == "RolledBack"),
                   "replaying the declared recovery does not issue a second compensation")
        self.snapshot("after replay")
        self.check(self.served() == "prior-a", "replay left the prior revision serving")

    def cell_tenant_bound_recovery(self):
        tenant_a = {"subject": "ops-a", "tenant": "tenant-a", "roles": ["admin", "platform_admin"]}
        tenant_b = {"subject": "ops-b", "tenant": "tenant-b", "roles": ["admin", "platform_admin"]}
        operation_id, op = self.activate("candidate-b", unique("tenant-bound"), principal=tenant_a)
        self.check(op["protection"].get("actor") == "ops-a", "grant sealed to the tenant-bound requesting actor")
        self.check(op["protection"].get("tenantId") == "tenant-a", "grant sealed to the requesting principal's tenant")

        # Identity binding is not opt-in: a principal that is not the sealed actor and holds no
        # platform role is refused even when it declares no fence at all.
        self.refused(operation_id, {"reason": "unfenced foreign principal"}, 403, "recovery_fence_actor_mismatch",
                     "unfenced rollback by a principal that is not the sealed actor")
        self.refused(operation_id, self.fence(op, tenantId="tenant-b"), 403, "recovery_fence_tenant_mismatch",
                     "sealed actor declaring a foreign tenant", principal=tenant_a)
        self.refused(operation_id, self.fence(op), 403, "recovery_fence_actor_mismatch",
                     "another tenant's platform administrator quoting the sealed grant", principal=tenant_b)
        self.check(self.served() == "candidate-b", "no refused recovery moved traffic")

        current = self.operation(operation_id, record=True)
        status, _ = self.http("POST", f"/api/v1/admin/deploy/operations/{operation_id}/rollback", self.fence(current), principal=tenant_a)
        self.check(status == 200, f"sealed tenant-bound actor's satisfied fence admitted (observed {status})")
        self.restored(operation_id, "tenant-bound declared recovery settled RolledBack")

    def cell_platform_admin_cross_tenant(self):
        # #4987 must refuse both original exploits without changing the operation or traffic.
        # Expectations come from the sealed-principal contract, not from observed responses.
        tenant_a = {"subject": "ops-a", "tenant": "tenant-a", "roles": ["admin", "platform_admin"]}
        tenant_b = {"subject": "ops-b", "tenant": "tenant-b", "roles": ["admin", "platform_admin"]}
        same_actor_other_tenant = {**tenant_b, "subject": "ops-a"}
        reader = {"subject": "ops-c", "tenant": "tenant-c", "roles": ["admin"]}
        operation_id, op = self.activate("candidate-b", unique("cross-tenant"), principal=tenant_a)
        self.check(op["protection"].get("actor") == "ops-a", "grant sealed to ops-a")
        self.check(op["protection"].get("tenantId") == "tenant-a", "grant sealed to tenant-a")
        before_replicas = self.replicas()
        for label, body in (
            ("complete fence quoting tenant-a's grant, declaring tenant-b's own identity",
             self.fence(op, actor="ops-b", tenantId="tenant-b")),
            ("unfenced rollback by tenant-b's platform administrator", {"reason": "unfenced cross-tenant compensation"}),
        ):
            self.refused(operation_id, body, 403, "recovery_fence_actor_mismatch", label, principal=tenant_b)
        self.refused(operation_id, self.fence(op, tenantId="tenant-b"), 403, "recovery_fence_tenant_mismatch",
                     "same actor name in another tenant", principal=same_actor_other_tenant)
        self.check(self.replicas() == before_replicas, "refused callers did not replace or retire either replica")
        self.check(self.served() == "candidate-b", "refused callers left the candidate serving")

        # Unprivileged status readers retain useful operation truth without the sealed identity.
        for path in (f"/api/v1/admin/deploy/operations/{operation_id}",
                     "/api/v1/admin/deploy/operations"):
            status, response = self.http("GET", path, principal=reader)
            self.check(status == 200, "tenant-bound status reader can read operation status")
            operations = response["items"] if "items" in response else [response]
            matching = [item for item in operations if item.get("operationId") == operation_id]
            self.check(len(matching) == 1, "status response includes the protected operation")
            protection = matching[0].get("protection") or {}
            for field in ("grantId", "actor", "tenantId"):
                self.check(not protection.get(field), f"status reader cannot read sealed {field}")
            self.check(protection.get("candidateRevision") == self.image("candidate-b"),
                       "redacted status preserves the candidate revision")

        current = self.operation(operation_id, record=True)
        status, _ = self.http("POST", f"/api/v1/admin/deploy/operations/{operation_id}/rollback",
                              self.fence(current), principal=tenant_a)
        self.check(status == 200, "sealed principal can still recover after the foreign callers were refused")
        self.restored(operation_id, "only the sealed principal restored the prior revision")

    def fault_cell(self, key, fault, label, expect_pending=False):
        operation_id, _ = self.activate("candidate-b", unique(key))
        self.telemetry(fault, label)
        if expect_pending:
            self.watch(operation_id, lambda o: reason(o) == "telemetry-evidence-pending" or o.get("status") == "RolledBack", 120,
                       "window held open on missing evidence")
        op = self.restored(operation_id, f"server reconciler recovered on {label}")
        self.check(not any(e["kind"] == "http" and e["request"]["path"].endswith("/rollback") for e in self.cell["events"]),
                   "driver issued no rollback request")
        return op

    def cell_error_rate_regression(self):
        self.fault_cell("error-rate", {**HEALTHY, "proof_error_rate": {"mode": "value", "value": "0.42"}},
                        "error-rate regression 0.42 > 0.05")

    def cell_latency_regression(self):
        self.fault_cell("latency", {**HEALTHY, "proof_latency_p95": {"mode": "value", "value": "2400"}},
                        "p95 latency regression 2400 ms > 500 ms")

    def cell_missing_telemetry(self):
        self.fault_cell("missing", {"*": {"mode": "empty"}}, "telemetry missing (empty vectors)", expect_pending=True)

    def cell_stale_telemetry(self):
        stale = {name: {**answer, "mode": "stale", "age_seconds": 900} for name, answer in HEALTHY.items()}
        self.fault_cell("stale", stale, "telemetry stale (samples 900 s old > 60 s bound)", expect_pending=True)

    def cell_wrong_body_private_probe(self):
        # A self-hosted target's only local correctness endpoint is the replica itself. The plan
        # admits a golden query there; the runtime SSRF guard then refuses the URL as
        # misconfigured, so a candidate with a CORRECT body is held and failed at the exposure
        # deadline. Recorded as a server finding: wrong-body regression cannot be gated locally.
        self.reset()
        self.telemetry(HEALTHY, "healthy evidence")
        extra = {
            "telemetry.golden_query.url": f"http://host.docker.internal:{STANDBY_PORT}/golden",
            "telemetry.golden_query.expected_contains": "candidate-b",
            "telemetry.exposure_deadline_seconds": "40",
        }
        status, plan = self.http("POST", "/api/v1/admin/deploy/plan", {
            "targetId": TARGET,
            "desiredRevision": self.image("candidate-b"),
            "currentRevision": self.image("prior-a"),
            "parameters": {**TELEMETRY_PARAMETERS, **extra},
        })
        self.check(status == 200, "plan evaluated")
        self.note("finding-plan", readyToSubmit=plan.get("readyToSubmit"), blockingReasons=plan.get("blockingReasons"))
        operation_id = self.create("candidate-b", unique("golden-private"), extra)
        op = self.watch(operation_id, lambda o: o.get("status") in ("RolledBack", "Failed", "ManualInterventionRequired")
                        or phase(o) == "observing", 150, "held on the refused golden query, then failed before exposure")
        self.check(phase(op) != "observing", "candidate was never activated")
        held = [e for e in self.cell["events"] if e["kind"] == "transition" and "golden-query" in (e.get("currentPhase") or "")]
        self.check(bool(held), "server reported the golden-query gate as misconfigured")
        self.snapshot("after the exposure deadline")
        self.check(self.served() == "prior-a", "prior revision kept serving")

    def cell_wrong_body_regression(self):
        # Because a private golden-query URL is refused (cell wrong-body-private-probe), the
        # wrong-body classification is exercised against a real public HTTPS 2xx response whose
        # body carries the configured wrong-result marker. The endpoint stands in for the
        # candidate's answer; what is proven is the gate's body verdict driving recovery.
        self.reset()
        self.telemetry(HEALTHY, "healthy metric evidence")
        extra = {
            "telemetry.golden_query.url": "https://example.com/",
            "telemetry.golden_query.expected_contains": "Example Domain",
            "telemetry.golden_query.forbidden_contains": "Example Domain",
            "telemetry.exposure_deadline_seconds": "60",
        }
        operation_id = self.create("candidate-b", unique("wrong-body"), extra)
        op = self.watch(operation_id, lambda o: o.get("status") in ("RolledBack", "Failed", "ManualInterventionRequired")
                        or phase(o) == "observing", 180, "wrong body refused")
        self.check(phase(op) != "observing", "candidate with a wrong body was never activated")
        self.check(op.get("status") == "RolledBack", "wrong body failed the rollout")
        verdicts = [e for e in self.cell["events"] if e["kind"] == "transition"
                    and "golden" in (e.get("currentPhase") or "").lower()]
        self.check(bool(verdicts), "server attributed the hold/failure to the golden-query body verdict")
        self.snapshot("after wrong-body refusal")
        self.check(self.served() == "prior-a", "prior revision kept serving")

    def cell_controller_crash(self):
        operation_id, before = self.activate("candidate-b", unique("crash"))
        self.note("fault", label="controller crash: server container restarted during the observation window")
        restart_server(self.work)
        after = self.watch(operation_id, lambda o: phase(o) == "observing", 120, "window survived the controller restart")
        for field in ("grantId", "firstExposureAt", "observationDeadline", "policyDigest", "previousRevision", "candidateRevision"):
            self.check(before["protection"][field] == after["protection"][field], f"restart preserved protection.{field}")
        self.snapshot("after controller restart")
        self.check(self.served() == "candidate-b", "candidate still serving after the controller restart")
        self.telemetry({**HEALTHY, "proof_error_rate": {"mode": "value", "value": "0.42"}}, "error-rate regression after restart")
        self.restored(operation_id, "restarted controller recovered from the persisted window")

    def cell_failed_recovery(self):
        operation_id, op = self.activate("candidate-b", unique("failed-recovery"))
        # Another writer replaces the retained prior replica with a revision nobody approved.
        foreign = self.image("candidate-c")
        docker("rm", "-f", f"{PREFIX}-app-{ACTIVE_PORT}")
        docker("run", "-d", "--name", f"{PREFIX}-app-{ACTIVE_PORT}", "-p", f"{ACTIVE_PORT}:8080",
               "--label", f"honua.target={TARGET}", "--label", "honua.role=active",
               "--label", f"honua.revision={foreign}", "--label", "io.honua.devops.proof.workload=candidate-c", foreign)
        self.note("fault", label="concurrent service change: retained prior replica replaced by an unapproved revision")
        self.snapshot("prior replica replaced out of band")
        self.telemetry({**HEALTHY, "proof_error_rate": {"mode": "value", "value": "0.42"}}, "error-rate regression")
        failed = self.watch(operation_id, lambda o: o.get("status") == "ManualInterventionRequired", 180,
                            "recovery could not be proven")
        protection = failed.get("protection") or {}
        self.check(protection.get("phase") == "unavailable", "failed recovery retains the window as unavailable")
        self.check(protection.get("reasonCode") == "rollback-failed", "reason code is rollback-failed")
        self.check(protection.get("previousRevision") == self.image("prior-a"), "retained window still names the prior revision")
        self.snapshot("after failed recovery")
        self.check(self.served() == "candidate-b", "the rejected candidate is still what serves (nothing was restored)")
        self.refused(operation_id, self.fence(op), 409, "recovery_fence_protection_phase_mismatch",
                     "stale observing-phase grant after the window became unavailable")

    def cell_newer_intent(self):
        operation_id, op = self.activate("candidate-b", unique("newer-intent"))
        newer_key = unique("newer-intent-c")
        status, newer = self.http("POST", "/api/v1/admin/deploy/operations", {
            "targetId": TARGET,
            "desiredRevision": self.image("candidate-c"),
            "currentRevision": self.image("candidate-b"),
            "reason": "concurrent approved change for the same target",
            "idempotencyKey": newer_key,
            "correlationId": newer_key,
            "submitImmediately": True,
            "parameters": TELEMETRY_PARAMETERS,
        })
        self.note("concurrent-change", status=status, response=newer)
        newer_id = newer.get("operationId") if isinstance(newer, dict) else None
        time.sleep(15)
        if newer_id:
            self.operation(newer_id, record=True)
        current = self.operation(operation_id, record=True)
        self.snapshot("after a concurrent change was submitted for the target")
        for field in ("grantId", "candidateRevision", "previousRevision", "policyDigest"):
            self.check(current["protection"][field] == op["protection"][field],
                       f"concurrent change left the first window's {field} intact")
        self.check(phase(current) == "observing", "first window still observing")
        status, _ = self.http("POST", f"/api/v1/admin/deploy/operations/{operation_id}/rollback", self.fence(current))
        self.check(status == 200, f"declared recovery of the first window still admitted (observed {status})")
        self.restored(operation_id, "first window recovered after the concurrent change")

    def cleanup(self):
        """Settles every non-terminal proof operation so it cannot act on the next cell's replicas."""
        status, listed = self.http("GET", "/api/v1/admin/deploy/operations?limit=50", record=False)
        items = listed if isinstance(listed, list) else (listed or {}).get("items", [])
        for op in items:
            if op.get("status") in ("Submitted", "Reconciling", "RollbackRequested", "Planned", "AwaitingApproval"):
                status, _ = self.http("POST", f"/api/v1/admin/deploy/operations/{op['operationId']}/rollback",
                                      {"reason": "proof cell cleanup"})
                self.note("cleanup", operation=op["operationId"], priorStatus=op.get("status"), rollbackStatus=status)
                if status == 200:
                    try:
                        self.watch(op["operationId"], lambda o: o.get("status") in ("RolledBack", "Failed", "ManualInterventionRequired", "Succeeded"),
                                   90, "cleanup settled")
                    except Failure:
                        pass


def phase(op):
    return (op.get("protection") or {}).get("phase")


def reason(op):
    return (op.get("protection") or {}).get("reasonCode")


def b64url(raw):
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()


def now():
    return iso(dt.datetime.now(dt.timezone.utc))


def iso(value):
    return value.isoformat().replace("+00:00", "Z")


def unique(label):
    return f"proof-{label}-{int(time.time())}"


def docker(*args, check=True):
    completed = subprocess.run(["docker", *args], capture_output=True, text=True)
    if check and completed.returncode != 0:
        raise Failure(f"docker {' '.join(args[:2])} failed: {completed.stderr.strip()}")
    return completed.stdout.strip()


def restart_server(work):
    here = __file__.rsplit("/", 1)[0]
    subprocess.run([f"{here}/boot.sh", "restart"], check=True, env={**os.environ, "PROOF_WORK": work})


CELLS = {
    "fenced-recovery": Journey.cell_fenced_recovery,
    "tenant-bound-recovery": Journey.cell_tenant_bound_recovery,
    "platform-admin-cross-tenant": Journey.cell_platform_admin_cross_tenant,
    "error-rate-regression": Journey.cell_error_rate_regression,
    "latency-regression": Journey.cell_latency_regression,
    "missing-telemetry": Journey.cell_missing_telemetry,
    "stale-telemetry": Journey.cell_stale_telemetry,
    "controller-crash": Journey.cell_controller_crash,
    "failed-recovery": Journey.cell_failed_recovery,
    "wrong-body-regression": Journey.cell_wrong_body_regression,
    "wrong-body-private-probe": Journey.cell_wrong_body_private_probe,
    "newer-intent": Journey.cell_newer_intent,
}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default="http://127.0.0.1:19191")
    parser.add_argument("--work", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--cells", default=",".join(CELLS))
    args = parser.parse_args()

    with open(f"{args.work}/admin-key", encoding="utf-8") as handle:
        key = handle.read().strip()
    journey = Journey(args.base, key, args.work)
    receipt = {"startedAt": now(), "cells": []}
    for name in args.cells.split(","):
        journey.cell = {"name": name, "startedAt": now(), "events": [], "assertions": []}
        try:
            CELLS[name](journey)
            journey.cell["result"] = "pass"
        except Failure as failure:
            journey.cell["result"] = "fail"
            journey.cell["failure"] = str(failure)
        journey.cleanup()
        journey.cell["finishedAt"] = now()
        receipt["cells"].append(journey.cell)
        print(f"{name}: {journey.cell['result']} {journey.cell.get('failure', '')}", flush=True)
        with open(args.out, "w", encoding="utf-8") as handle:
            json.dump(receipt, handle, indent=2)
    receipt["finishedAt"] = now()
    with open(args.out, "w", encoding="utf-8") as handle:
        json.dump(receipt, handle, indent=2)
    return 0 if all(cell["result"] == "pass" for cell in receipt["cells"]) else 1


if __name__ == "__main__":
    sys.exit(main())
