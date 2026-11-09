"""Validate the public AI policy manifest without reading local context or secrets."""
import json
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parent.parent
policy = json.loads((root / "ai-security-gate.json").read_text())
assert policy["version"] == 1
assert policy["checks"]["secrets"]["redact"] is True
for path in [policy["checks"]["publicTree"], policy["checks"]["secrets"]["config"]]:
    assert (root / path).is_file(), "Missing security gate implementation"
for path in policy["requiredIgnoredPaths"]:
    result = subprocess.run(["git", "check-ignore", "--no-index", "--quiet", path], cwd=root)
    assert result.returncode == 0, "Local context or secret path is not ignored"
print("AI policy gate passed (no private context read).")
