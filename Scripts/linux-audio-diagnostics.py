#!/usr/bin/env python3
"""Read-only audio flight recorder. Never opens PCM or changes routing/settings."""
import argparse
from datetime import datetime, timezone
import json
import logging
from logging.handlers import RotatingFileHandler
import os
from pathlib import Path
import signal
import socket
import subprocess
import threading
import time


def parse_top(line):
    fields = line.split(None, 9)
    if len(fields) != 10 or fields[0] not in ('R', 'S', 'I', 'E', 'C'):
        return None
    try:
        return dict(state=fields[0], id=int(fields[1]), quantum=int(fields[2]),
                    rate=int(fields[3]), wait=fields[4], busy=fields[5],
                    waitRatio=fields[6], busyRatio=fields[7], errors=int(fields[8]),
                    formatAndName=fields[9].strip())
    except ValueError:
        return None


def error_delta(previous, current):
    # C is pw-top's initial placeholder, not an observed zero counter.
    if not previous or previous['state'] == 'C' or current['state'] == 'C':
        return None
    if previous['formatAndName'] != current['formatAndName']:
        return None
    value = current['errors'] - previous['errors']
    return value if value > 0 else None


def engine_status(path):
    with socket.socket(socket.AF_UNIX) as connection:
        connection.settimeout(1)
        connection.connect(str(path))
        connection.sendall(b'{"id":"diagnostics","command":"status","args":{}}\n')
        with connection.makefile('rb') as reader:
            line = reader.readline(1024 * 1024 + 1)
        if len(line) > 1024 * 1024:
            raise ValueError('oversized status response')
    reply = json.loads(line)
    if reply.get('id') != 'diagnostics' or not reply.get('ok'):
        raise ValueError('invalid status response')
    state = reply['data']
    # No filter filenames, audio levels, content, or entire config dumps.
    keys = ('status', 'sampleRate', 'eqEnabled', 'aecEnabled', 'activeBackend',
            'degraded', 'degradedReason', 'captureConnected', 'referenceConnected',
            'outputId', 'inputId', 'npu', 'diagnostics')
    result = {k: state[k] for k in keys if k in state}
    result['diagnostics'] = {k: v for k, v in result.get('diagnostics', {}).items()
                             if k != 'reductionDb'}
    return result


def graph_snapshot():
    raw = subprocess.run(['pw-dump'], capture_output=True, text=True, timeout=3,
                         check=True).stdout
    keys = ('node.name', 'node.driver-id', 'object.serial', 'media.class',
            'audio.rate', 'node.latency', 'api.alsa.headroom', 'api.alsa.period-size',
            'api.alsa.path', 'api.alsa.pcm.card', 'device.bus', 'priority.driver')
    nodes = []
    for item in json.loads(raw):
        info = item.get('info', {})
        props = info.get('props', {})
        if item.get('type') == 'PipeWire:Interface:Node' and 'Audio' in props.get('media.class', ''):
            nodes.append(dict(id=item['id'], state=info.get('state'),
                              props={k: props[k] for k in keys if k in props}))
    return nodes


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, default=Path(os.environ.get(
        'XDG_STATE_HOME', str(Path.home() / '.local/state'))) / 'personal-tools/audio-diagnostics')
    parser.add_argument('--seconds', type=float, default=0, help='0 runs until stopped')
    args = parser.parse_args()
    os.umask(0o077)
    args.directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    handler = RotatingFileHandler(args.directory / 'events.jsonl', maxBytes=8*1024*1024,
                                  backupCount=4, encoding='utf-8')
    handler.setFormatter(logging.Formatter('%(message)s'))
    logger = logging.getLogger('audio-diagnostics')
    logger.setLevel(logging.INFO)
    logger.addHandler(handler)
    stop = threading.Event()
    children = []
    child_lock = threading.Lock()
    def emit(kind, **data):
        logger.info(json.dumps(dict(at=datetime.now(timezone.utc).isoformat(),
                                   monotonic=time.monotonic(), kind=kind, **data), ensure_ascii=False))
    def follow(name, command, consume):
        while not stop.is_set():
            child = None
            try:
                child = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                         text=True, env={**os.environ, 'LC_ALL': 'C'})
                with child_lock:
                    children.append(child)
                emit('collector-start', collector=name, pid=child.pid)
                for line in child.stdout:
                    if stop.is_set():
                        break
                    consume(line.rstrip())
                emit('collector-exit', collector=name, returncode=child.poll())
            except Exception as error:
                emit('collector-error', collector=name, error=str(error))
            finally:
                if child is not None:
                    child.terminate() if child.poll() is None else None
                    try:
                        child.wait(timeout=2)
                    except subprocess.TimeoutExpired:
                        child.kill()
                        child.wait()
                    with child_lock:
                        children.remove(child)
            stop.wait(3)

    previous = {}
    sampled = {}
    def top_line(line):
        row = parse_top(line)
        if row is None:
            if line and not line.startswith('S   ID'):
                emit('top-message', message=line[:2048])
            return
        node = row['id']
        delta = error_delta(previous.get(node), row)
        now = time.monotonic()
        if delta:
            emit('xrun-increase', delta=delta, node=row)
        if now - sampled.get(node, 0) >= 5:
            emit('node-sample', node=row)
            sampled[node] = now
        previous[node] = row

    def journal_line(source, line):
        try:
            record = json.loads(line)
            emit('journal', source=source, timestamp=record.get('__REALTIME_TIMESTAMP'),
                 message=record.get('MESSAGE'), unit=record.get('_SYSTEMD_USER_UNIT'))
        except ValueError:
            emit('journal-message', source=source, message=line[:2048])

    commands = [
        ('pw-top', ['stdbuf', '-oL', 'pw-top', '-b'], top_line),
        ('audio-journal', ['journalctl', '--user', '-f', '-n', '0', '-o', 'json',
                           '-u', 'pipewire', '-u', 'pipewire-pulse', '-u', 'wireplumber',
                           '-u', 'personal-tools-audio', '-u', 'personal-tools'],
         lambda line: journal_line('audio', line)),
        ('kernel-journal', ['journalctl', '-k', '-f', '-n', '0', '-o', 'json',
                            '--grep=usb |xhci|snd[_-]|thunderbolt|watchdog|soft lockup|workqueue:.*hogged'],
         lambda line: journal_line('kernel', line)),
    ]
    for sig in (signal.SIGTERM, signal.SIGINT):
        signal.signal(sig, lambda *_: stop.set())
    emit('start', bootId=Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
         version=1, recordsAudio=False)
    threads = [threading.Thread(target=follow, args=command, daemon=True) for command in commands]
    for thread in threads:
        thread.start()
    started = time.monotonic()
    next_graph = 0
    try:
        while not stop.is_set() and (not args.seconds or time.monotonic()-started < args.seconds):
            try:
                runtime = Path(os.environ['XDG_RUNTIME_DIR']) / 'personal-tools/audio.sock'
                emit('engine-sample', status=engine_status(runtime))
            except Exception as error:
                emit('engine-unavailable', error=str(error))
            pressure = {}
            for name in ('cpu', 'io', 'memory'):
                try:
                    pressure[name] = Path('/proc/pressure', name).read_text().strip()
                except OSError:
                    pass
            emit('system-sample', load=os.getloadavg(), pressure=pressure)
            if time.monotonic() >= next_graph:
                try:
                    emit('graph', nodes=graph_snapshot())
                except Exception as error:
                    emit('graph-error', error=str(error))
                next_graph = time.monotonic() + 30
            stop.wait(2)
    finally:
        stop.set()
        with child_lock:
            for child in children:
                if child.poll() is None:
                    child.terminate()
        for thread in threads:
            thread.join(timeout=4)
        emit('stop')
        handler.close()


if __name__ == '__main__':
    main()
