"""Execute the result page's real curl examples against a local capture API."""
import html.parser
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Commands(html.parser.HTMLParser):
    def __init__(self, source):
        super().__init__(convert_charrefs=True)
        self.commands = {}
        self.current = None
        self.feed(source)

    def handle_starttag(self, tag, attributes):
        if tag == "code":
            self.current = dict(attributes).get("id")
            if self.current:
                self.commands[self.current] = ""

    def handle_endtag(self, tag):
        if tag == "code":
            self.current = None

    def handle_data(self, data):
        if self.current:
            self.commands[self.current] += data


received = []


class Capture(BaseHTTPRequestHandler):
    def do_POST(self):
        received.append((self.headers.get("Content-Type"), self.rfile.read(int(self.headers["Content-Length"]))))
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b"ok\n")

    def log_message(self, *args):
        pass


base = os.environ.get("TIMEBIN_BASE_URL", "http://localhost:8080")
with urllib.request.urlopen(base + "/health/live", timeout=10) as response:
    assert response.headers.get("Server") is None, "Server implementation header must be disabled"


def commands_for(content):
    request = urllib.request.Request(base + "/api/v1/pastes", data=json.dumps({"content": content}).encode(), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=10) as response:
        created = json.load(response)
    request = urllib.request.Request(base + "/" + created["slug"], headers={"Accept": "text/html"})
    with urllib.request.urlopen(request, timeout=10) as response:
        return Commands(response.read().decode()).commands


server = ThreadingHTTPServer(("127.0.0.1", 0), Capture)
worker = threading.Thread(target=server.serve_forever, daemon=True)
worker.start()
endpoint = f"http://127.0.0.1:{server.server_port}/capture"
scratch = Path.cwd() / "work"
scratch.mkdir(exist_ok=True)
try:
    with tempfile.TemporaryDirectory(prefix="timebin-examples-", dir=scratch) as directory:
        folder = Path(directory)

        def run(command, success=True):
            result = subprocess.run(["bash", "-c", command.replace("https://api.example.com/endpoint", endpoint)], cwd=folder, capture_output=True)
            assert (result.returncode == 0) == success, result.stderr.decode(errors="replace")

        content = '"quoted"\r\n中文 🌍\n$(touch should-not-exist)\n<untrusted>'
        commands = commands_for(content)
        run(commands["save-file"])
        assert (folder / "paste.txt").read_bytes() == content.encode()
        run(commands["post-text"])
        assert received[-1] == ("text/plain; charset=utf-8", content.encode())
        run(commands["post-field"])
        assert received[-1][0] == "application/json"
        assert json.loads(received[-1][1]) == {"content": content}
        assert not (folder / "should-not-exist").exists()
        for document in ['{"message":"Olá 中文","enabled":true}', "null", "false"]:
            run(commands_for(document)["post-json"])
            assert received[-1] == ("application/json", document.encode())
        count = len(received)
        for invalid in ["not JSON", "{} {}"]:
            run(commands_for(invalid)["post-json"], success=False)
        assert len(received) == count, "Invalid JSON must not be forwarded"
    print("Result-page examples passed: exact UTF-8, JSON escaping, JSON validation, and no text execution.")
finally:
    server.shutdown()
    server.server_close()
