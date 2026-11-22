import json
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

COUNTS = {"a": 0, "b": 0}
LAST = {"a": 0.0, "b": 0.0}
STARTED = time.time()


def page(name: str, marker: str) -> bytes:
    request_id = f"{name}-{COUNTS[name]}-{int(time.time() * 1000)}"
    return f"""<!doctype html>
<html><head><meta charset="utf-8"><title>AMARE fixture {marker.upper()}</title></head>
<body data-fixture="{marker}" data-request-id="{request_id}">
<h1 id="ready">fixture-{marker}</h1>
<p id="request-id">{request_id}</p>
</body></html>""".encode("utf-8")


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass

    def _send(self, code: int, body: bytes, content_type: str):
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store, no-cache, must-revalidate")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        path = urlparse(self.path).path
        if path == "/ready":
            self._send(200, b"ready\n", "text/plain; charset=utf-8")
            return
        if path == "/stats":
            payload = json.dumps({
                "a": COUNTS["a"],
                "b": COUNTS["b"],
                "last_a_ms": LAST["a"],
                "last_b_ms": LAST["b"],
                "uptime_ms": int((time.time() - STARTED) * 1000),
            }).encode("utf-8")
            self._send(200, payload, "application/json")
            return
        if path == "/fixture-a.html":
            COUNTS["a"] += 1
            LAST["a"] = time.time() * 1000
            self._send(200, page("a", "a"), "text/html; charset=utf-8")
            return
        if path == "/fixture-b.html":
            COUNTS["b"] += 1
            LAST["b"] = time.time() * 1000
            self._send(200, page("b", "b"), "text/html; charset=utf-8")
            return
        self._send(404, b"not found\n", "text/plain; charset=utf-8")


if __name__ == "__main__":
    import sys
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8774
    ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
