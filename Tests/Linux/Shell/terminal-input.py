#!/usr/bin/python3
"""Collect synthetic paste bytes in a private test PTY; never execute input."""
import os
from pathlib import Path
import select
import sys
import termios
import time
import tty

fd = sys.stdin.fileno()
saved = termios.tcgetattr(fd)
try:
    tty.setraw(fd)
    Path(sys.argv[2]).touch()
    data = bytearray()
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if select.select([fd], [], [], 0.1)[0]:
            chunk = os.read(fd, 4096)
            if not chunk:
                break
            data.extend(chunk)
            Path(sys.argv[1]).write_bytes(data)
finally:
    termios.tcsetattr(fd, termios.TCSANOW, saved)
