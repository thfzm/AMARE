import http.server
import os
import sys
import time

root = os.path.abspath(sys.argv[1])
port = int(sys.argv[2]) if len(sys.argv) > 2 else 8765

class Handler(http.server.SimpleHTTPRequestHandler):
    def translate_path(self, path):
        return super().translate_path(path)
    def copyfile(self, source, outputfile):
        if self.path.endswith('.bin'):
            while True:
                chunk = source.read(65536)
                if not chunk:
                    break
                outputfile.write(chunk)
                outputfile.flush()
                time.sleep(0.08)
        else:
            super().copyfile(source, outputfile)
    def log_message(self, *args):
        pass

os.chdir(root)
http.server.ThreadingHTTPServer(('127.0.0.1', port), Handler).serve_forever()
