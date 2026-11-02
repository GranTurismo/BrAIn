#!/usr/bin/env python3
"""A stand-in for api.github.com and github.com's release downloads, to test the self-updater end to end.

    fake-github.py <dir> <cert.pem> <key.pem> [port]

Serves HTTPS (default port 443; the rig points api.github.com and github.com at it):
  GET /repos/GranTurismo/BrAIn/releases/latest             answers <dir>/latest.json (read on every request, so
                                                           editing it changes the "latest release" at once)
  GET /GranTurismo/BrAIn/releases/download/<tag>/<name>    sends <dir>/files/<name>, or 404
Every request is appended to <dir>/requests.log. Downloads are paced (about 25 MB/s) so a progress bar has time to show.
"""
import http.server, os, ssl, sys, time

root, cert, key = sys.argv[1], sys.argv[2], sys.argv[3]
port = int(sys.argv[4]) if len(sys.argv) > 4 else 443


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def log_message(self, fmt, *args):
        with open(os.path.join(root, 'requests.log'), 'a') as f:
            f.write('%s %s\n' % (self.command, self.path))

    def reply(self, code, body, ctype='application/octet-stream'):
        self.send_response(code)
        self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        for i in range(0, len(body), 512 * 1024):
            self.wfile.write(body[i:i + 512 * 1024])
            if len(body) > 1024 * 1024:
                time.sleep(0.02)

    def do_GET(self):
        path = self.path.split('?')[0]
        if path == '/repos/GranTurismo/BrAIn/releases/latest':
            return self.reply(200, open(os.path.join(root, 'latest.json'), 'rb').read(), 'application/json')
        parts = path.strip('/').split('/')
        if len(parts) == 6 and parts[:4] == ['GranTurismo', 'BrAIn', 'releases', 'download']:
            f = os.path.join(root, 'files', os.path.basename(parts[5]))
            if os.path.isfile(f):
                return self.reply(200, open(f, 'rb').read())
        self.reply(404, b'not found', 'text/plain')


server = http.server.ThreadingHTTPServer(('0.0.0.0', port), Handler)
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(cert, key)
server.socket = context.wrap_socket(server.socket, server_side=True)
server.serve_forever()
