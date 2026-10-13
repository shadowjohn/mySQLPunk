"""Local-only, allowlisted server for the MSSQL export UI prototype.

No database access, file uploads, or directory listing. Stop with Ctrl+C.
"""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit
import argparse

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
ROUTES = {
    "/": (HERE / "index.html", "text/html; charset=utf-8"),
    "/docs/design/mssql-export/index.html": (HERE / "index.html", "text/html; charset=utf-8"),
}
for name, mime in (
    ("punky_report_loading_atlas.png", "image/png"),
    ("punky_report_loading.png", "image/png"),
    ("punky_report_loading.wav", "audio/wav"),
):
    ROUTES["/mySQLPunk/image/" + name] = (ROOT / "mySQLPunk" / "image" / name, mime)


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        path = urlsplit(self.path).path
        if path == "/":
            self.send_response(302)
            self.send_header("Location", "/docs/design/mssql-export/index.html")
            self.end_headers()
            return
        resource = ROUTES.get(path)
        if resource is None or not resource[0].is_file():
            self.send_error(404)
            return
        file, mime = resource
        self.send_response(200)
        self.send_header("Content-Type", mime)
        self.send_header("Content-Length", str(file.stat().st_size))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        try:
            with file.open("rb") as stream:
                while chunk := stream.read(65536):
                    self.wfile.write(chunk)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def log_message(self, *_):
        pass


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=17643)
    args = parser.parse_args()
    print(f"Preview: http://127.0.0.1:{args.port}/docs/design/mssql-export/index.html", flush=True)
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
