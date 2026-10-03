#!/usr/bin/env python3
"""Owned HTTPS test fixture (spec §10). Serves the isolation page on two origins and echoes request headers.

Usage:
  1. Create a locally trusted certificate for localhost and 127.0.0.1 (for example with mkcert):
       mkcert -install && mkcert -cert-file cert.pem -key-file key.pem localhost 127.0.0.1
     Never disable certificate validation in the application instead.
  2. python fixture_server.py --cert cert.pem --key key.pem
  3. Debug build only:
       set PP_FIXTURE_ORIGINS=https://localhost:8443;https://127.0.0.1:8444
       set PP_FIXTURE_START=https://localhost:8443/
Every request is logged with method, path, User-Agent and Accept-Language so HTTP-level values can be compared
with the in-page navigator/Intl values (A06, A28). Synthetic data only.
"""
import argparse, http.server, json, os, ssl, threading, functools, datetime

SITE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "site")

class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, fmt, *args):
        print(json.dumps({
            "t": datetime.datetime.utcnow().isoformat() + "Z",
            "origin_port": self.server.server_port,
            "method": self.command, "path": self.path.split("?")[0],
            "ua": self.headers.get("User-Agent"),
            "accept_language": self.headers.get("Accept-Language"),
            "sec_ch_ua": self.headers.get("Sec-CH-UA"),
            "client": self.client_address[0],
        }, ensure_ascii=False), flush=True)

    def do_GET(self):
        if self.path.startswith("/echo-headers") or self.path.startswith("/worker-ping"):
            body = json.dumps({k: v for k, v in self.headers.items() if k.lower() in ("user-agent", "accept-language", "sec-ch-ua", "sec-ch-ua-platform")}, ensure_ascii=False).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)
            return
        return super().do_GET()

def serve(port, cert, key):
    httpd = http.server.ThreadingHTTPServer(("127.0.0.1", port), functools.partial(Handler, directory=SITE))
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.load_cert_chain(cert, key)
    httpd.socket = ctx.wrap_socket(httpd.socket, server_side=True)
    print(f"fixture listening on https://127.0.0.1:{port}/", flush=True)
    httpd.serve_forever()

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--cert", required=True)
    ap.add_argument("--key", required=True)
    ap.add_argument("--ports", default="8443,8444")
    a = ap.parse_args()
    ports = [int(p) for p in a.ports.split(",")]
    for p in ports[1:]:
        threading.Thread(target=serve, args=(p, a.cert, a.key), daemon=True).start()
    serve(ports[0], a.cert, a.key)
