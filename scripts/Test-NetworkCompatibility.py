"""Offline process-level regressions for the bundled HTTPS helper.

Injects deterministic transport fixtures into a temporary copy. No network,
production helper modification, credentials or live Codex configuration.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


FIXTURE = r'''
class FixtureSocket:
    def settimeout(self, value):
        pass

class FixtureResponse:
    status = 200
    headers = http.client.HTTPMessage()
    headers['Content-Length'] = '4'
    def __init__(self):
        from types import SimpleNamespace
        self.fp = SimpleNamespace(raw=SimpleNamespace(_sock=FixtureSocket()))
        self.reads = 0
    def __enter__(self):
        return self
    def __exit__(self, *args):
        pass
    def read1(self, count):
        if os.environ['EGG_FIXTURE'] == 'read_stall':
            time.sleep(3)
        if self.reads == 4:
            return b''
        time.sleep(0.4)
        self.reads += 1
        return b'x'

class FixtureClient:
    def open(self, request, timeout):
        if os.environ['EGG_FIXTURE'] == 'request_stall':
            time.sleep(3)
        return FixtureResponse()

urllib.request.build_opener = lambda *args: FixtureClient()
'''


class NetworkCompatibilityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workspace = tempfile.TemporaryDirectory(prefix="egg-network-check-")
        cls.root = Path(cls.workspace.name)
        source = Path(__file__).resolve().parents[1] / "src/Launcher.ChatGPT/Networking/egg-network.py"
        cls.helper = cls.root / "egg-network.py"
        text = source.read_text(encoding="utf-8")
        cls.helper.write_text(text.replace('if __name__ == "__main__":', FIXTURE + '\nif __name__ == "__main__":'), encoding="utf-8")

    @classmethod
    def tearDownClass(cls):
        cls.workspace.cleanup()

    def run_helper(self, scenario, *args):
        environment = dict(os.environ, EGG_FIXTURE=scenario)
        for name in ("HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY"):
            environment.pop(name, None)
        process = subprocess.run([sys.executable, str(self.helper), "https://fixture.invalid/", *args],
                                 capture_output=True, text=True, encoding="utf-8", env=environment, timeout=15)
        self.assertEqual(len(process.stdout.splitlines()), 1, process.stderr)
        result = json.loads(process.stdout)
        self.assertEqual(process.returncode, 0 if result["ok"] else 1)
        return result

    def test_request_budget(self):
        result = self.run_helper("request_stall", "--timeout", "5", "--request-timeout", "1")
        self.assertEqual((result["code"], result["timeout_phase"]), ("timeout", "request"))

    def test_idle_read_budget_cleans_partial_file(self):
        destination = self.root / "stalled.bin"
        result = self.run_helper("read_stall", "--timeout", "5", "--read-timeout", "1", "--output", str(destination))
        self.assertEqual((result["code"], result["timeout_phase"]), ("timeout", "read"))
        self.assertTrue(result["temporary_file_removed"])
        self.assertFalse(destination.exists())
        self.assertEqual(list(self.root.glob(".egg-download-*")), [])

    def test_continuous_transfer_exceeds_timeout_and_succeeds(self):
        destination = self.root / "complete.bin"
        result = self.run_helper("stream", "--timeout", "1", "--output", str(destination))
        self.assertTrue(result["ok"])
        self.assertEqual(destination.read_bytes(), b"xxxx")
        self.assertEqual(result["bytes_read"], 4)
        self.assertEqual(len(result["sha256"]), 64)

    def test_new_read_setting_overrides_fallback(self):
        result = self.run_helper("stream", "--timeout", "1", "--read-timeout", "3")
        self.assertTrue(result["ok"])

    def test_head(self):
        self.assertTrue(self.run_helper("stream", "--head", "--timeout", "1")["ok"])

    def test_existing_file_is_not_overwritten(self):
        destination = self.root / "existing.bin"
        destination.write_bytes(b"original")
        result = self.run_helper("stream", "--output", str(destination))
        self.assertEqual(result["code"], "destination_exists")
        self.assertEqual(destination.read_bytes(), b"original")

    def test_download_size_bound(self):
        destination = self.root / "oversize.bin"
        result = self.run_helper("stream", "--max-bytes", "3", "--output", str(destination))
        self.assertEqual(result["code"], "download_too_large")
        self.assertFalse(destination.exists())

    def test_invalid_timeout(self):
        self.assertEqual(self.run_helper("stream", "--read-timeout", "0")["code"], "invalid_request")


if __name__ == "__main__":
    unittest.main(verbosity=2)
