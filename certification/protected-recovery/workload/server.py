"""Minimal serving workload rolled by the self-hosted rolling deploy backend.

Each build bakes a distinct REVISION into the image, so the prior and candidate
revisions are two different immutable image ids. /healthz/ready answers the
readiness shape the backend's local health gate expects.
"""
import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

REVISION = os.environ.get("WORKLOAD_REVISION", "unset")


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path.startswith("/healthz"):
            body = {"status": "Healthy", "revision": REVISION}
        else:
            body = {"revision": REVISION, "path": self.path}
        payload = json.dumps(body).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *_):
        pass


ThreadingHTTPServer(("0.0.0.0", 8080), Handler).serve_forever()
