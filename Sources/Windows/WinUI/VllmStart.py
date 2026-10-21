"""Stream this script to the existing WSL venv; model files never cross Windows/WSL."""
import fcntl
import json
import os
from pathlib import Path
import subprocess
import threading
import time


def main():
    output_lock = threading.Lock()

    def emit(source, line):
        with output_lock:
            print(json.dumps({"source": source, "line": line}, ensure_ascii=True), flush=True)

    with open(".personal-tools-start.lock", "a") as lock:
        deadline = time.monotonic() + 900
        emit("bridge", "waiting")
        while True:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except BlockingIOError:
                if time.monotonic() >= deadline:
                    raise TimeoutError("Another startup is still in progress")
                time.sleep(0.25)
        emit("bridge", "locked")
        path = Path("server.log")
        # Read only this launch's appended bytes, never replay old successful startup logs.
        position = path.stat().st_size if path.exists() else 0
        inode = path.stat().st_ino if path.exists() else None
        pending = b""

        def read_log():
            nonlocal position, inode, pending
            try:
                with path.open("rb") as stream:
                    current = os.fstat(stream.fileno())
                    if current.st_ino != inode or current.st_size < position:
                        position, pending = 0, b""
                    inode = current.st_ino
                    stream.seek(position)
                    chunk = stream.read(65536)
                    position = stream.tell()
                parts = (pending + chunk).replace(b"\r", b"\n").split(b"\n")
                pending = parts.pop()[-65536:]
                for line in parts:
                    if line:
                        emit("log", line.decode("utf-8", errors="replace"))
            except FileNotFoundError:
                pass

        child = subprocess.Popen(["./start.sh"], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                 text=True, encoding="utf-8", errors="replace", bufsize=1,
                                 env={**os.environ, "PYTHONUNBUFFERED": "1"})

        def pump():
            for line in child.stdout:
                emit("stdout", line.rstrip("\r\n"))

        reader = threading.Thread(target=pump, daemon=True)
        reader.start()
        deadline = time.monotonic() + 1200
        try:
            while child.poll() is None:
                read_log()
                if time.monotonic() >= deadline:
                    raise TimeoutError("Startup or warmup exceeded 20 minutes")
                time.sleep(0.25)
            reader.join(timeout=5)
            read_log()
            return child.returncode
        finally:
            if child.poll() is None:
                child.terminate()  # The systemd model service remains independent.
                try:
                    child.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    child.kill()


if __name__ == "__main__":
    raise SystemExit(main())
