import http.server
import sys
import time

port = int(sys.argv[1]) if len(sys.argv) > 1 else 8772

FAST = b"<!doctype html><title>FAST</title><h1>fast</h1>"
SLOW = b"<!doctype html><title>SLOW</title><h1>slow</h1>"
SCROLL = b"""<!doctype html><meta charset='utf-8'><title>SCROLL</title>
<style>body{height:6000px;font:24px sans-serif;background:#eef}</style>
<h1>scroll test</h1><script>
const sync=()=>{document.title='SCROLL:'+Math.round(scrollY)};
addEventListener('scroll',sync);addEventListener('load',sync);sync();
</script>"""

class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path.startswith('/slow'):
            time.sleep(8)
            body = SLOW
        elif self.path.startswith('/scroll'):
            body = SCROLL
        else:
            body = FAST
        self.send_response(200)
        self.send_header('Content-Type', 'text/html; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def log_message(self, *args):
        pass

http.server.ThreadingHTTPServer(('127.0.0.1', port), Handler).serve_forever()
