#!/usr/bin/env python3
"""Tiny HTTPS stand-in for the GitHub releases API + release downloads, for the update smoke only.

Usage: update-server.py SERVE_DIR CERT KEY [PORT]
  GET /repos/<owner>/<name>/releases?per_page=N -> SERVE_DIR/releases.json (a GitHub-style release listing)
  GET /download/<file>                          -> SERVE_DIR/download/<file>
The smoke script swaps those files to "publish" a new release. Listens on 127.0.0.1 only.
"""
import http.server
import os
import re
import ssl
import sys


class Handler(http.server.BaseHTTPRequestHandler):
    serve_dir = ""

    def _send(self, path, content_type):
        try:
            with open(path, "rb") as f:
                body = f.read()
        except OSError:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):  # noqa: N802
        route = self.path.split("?", 1)[0]
        if re.fullmatch(r"/repos/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/releases", route):
            self._send(os.path.join(self.serve_dir, "releases.json"), "application/json")
        elif re.fullmatch(r"/download/[A-Za-z0-9_.-]+", route):
            self._send(os.path.join(self.serve_dir, "download", route.rsplit("/", 1)[1]), "application/octet-stream")
        else:
            self.send_error(404)

    def log_message(self, fmt, *args):
        sys.stderr.write("update-server: %s\n" % (fmt % args))


def main():
    Handler.serve_dir = os.path.abspath(sys.argv[1])
    port = int(sys.argv[4]) if len(sys.argv) > 4 else 8443
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(sys.argv[2], sys.argv[3])
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler)
    server.socket = context.wrap_socket(server.socket, server_side=True)
    server.serve_forever()


main()
