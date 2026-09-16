"""Prometheus query API stub whose answers are the injected fault.

The deploy telemetry gate queries `/api/v1/query?query=<expr>`. The answer for
each expression is read from /control/state.json on every request, so the proof
driver switches between healthy, error-rate breach, latency breach, missing
(empty vector) and stale (old sample timestamp) evidence without restarting
anything. Every query is appended to /control/queries.jsonl as evidence that
the server consumed it.
"""
import json
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

STATE = "/control/state.json"
LOG = "/control/queries.jsonl"


def load_state():
    try:
        with open(STATE, encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return {}


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path == "/-/ready":
            return self.reply(200, {"status": "ready"})
        if parsed.path != "/api/v1/query":
            return self.reply(404, {"status": "error", "error": "not found"})
        query = urllib.parse.parse_qs(parsed.query).get("query", [""])[0]
        state = load_state()
        answer = state.get(query, state.get("*", {"mode": "empty"}))
        mode = answer.get("mode", "value")
        now = time.time()
        if mode == "empty":
            result = []
        else:
            stamp = now - float(answer.get("age_seconds", 0)) if mode == "stale" else now
            result = [{"metric": {}, "value": [stamp, str(answer.get("value", "0"))]}]
        with open(LOG, "a", encoding="utf-8") as handle:
            handle.write(json.dumps({"at": now, "query": query, "mode": mode, "result": result}) + "\n")
        return self.reply(200, {"status": "success", "data": {"resultType": "vector", "result": result}})

    def reply(self, status, body):
        payload = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *_):
        pass


ThreadingHTTPServer(("0.0.0.0", 9090), Handler).serve_forever()
