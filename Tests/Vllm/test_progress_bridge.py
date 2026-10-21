"""Run inside WSL with observer_source injected; fixtures stay entirely in Linux tmp."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


class ProgressBridgeTests(unittest.TestCase):
    def run_fixture(self, content, old_log="OLD_READY\n"):
        with tempfile.TemporaryDirectory(prefix="personal-tools-vllm-test-") as folder:
            root = Path(folder)
            (root / "server.log").write_text(old_log)
            script = root / "start.sh"
            script.write_text("#!" + sys.executable + "\n" + content)
            script.chmod(0o700)
            result = subprocess.run([sys.executable, "-u", "-"], input=observer_source,
                                    cwd=folder, text=True, capture_output=True, timeout=15)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            return result, events

    def test_live_append_cr_progress_and_warmup(self):
        result, events = self.run_fixture('''import time
with open("server.log", "a") as log:
    log.write("Loading safetensors checkpoint shards: 20% Completed | 1/5\\r")
    log.flush()
    time.sleep(.4)
    log.write("Loading safetensors checkpoint shards: 100% Completed | 5/5\\n")
    log.flush()
print("Ready: local", flush=True)
print("Chat warmup 1/2: 1.0s", flush=True)
print("Chat warmup 2/2: 0.5s", flush=True)
''')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(events[0], {"source": "bridge", "line": "waiting"})
        self.assertTrue(any("20%" in event["line"] for event in events))
        self.assertTrue(any("100%" in event["line"] for event in events))
        self.assertTrue(any(event["source"] == "stdout" and "2/2" in event["line"] for event in events))
        self.assertFalse(any("OLD_READY" in event["line"] for event in events))

    def test_log_truncation_and_child_error(self):
        result, events = self.run_fixture('''import sys, time
with open("server.log", "w") as log:
    log.write("new-start\\n")
print("warmup failed", flush=True)
time.sleep(.4)
sys.exit(7)
''', "OLD_READY\n" * 100)
        self.assertEqual(result.returncode, 7)
        self.assertTrue(any(event["line"] == "new-start" for event in events))
        self.assertTrue(any(event["line"] == "warmup failed" for event in events))
        self.assertFalse(any("OLD_READY" in event["line"] for event in events))


unittest.main(argv=["progress-bridge"], verbosity=2)
