#!/usr/bin/env python3
"""Measure real NPU/PipeWire processing without retaining microphone samples.

Owns a private engine and consumes its microphone into memory only long enough
to count frames and nonfinite values. No audio is written to disk. A silent
playback stream exercises EQ. Existing defaults, mute states and streams remain
untouched. Device IDs must be explicit; results distinguish soak from AEC quality.
"""
import argparse
import array
from datetime import datetime, timezone
import hashlib
import json
import math
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time
import traceback


def defaults():
    graph = json.loads(subprocess.check_output(['pw-dump'], text=True))
    return [item.get('metadata', []) for item in graph
            if item.get('type') == 'PipeWire:Interface:Metadata'
            and item.get('props', {}).get('metadata.name') == 'default']


def run(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    runtime = json.loads(args.runtime.read_text())
    env = os.environ.copy()
    env['LD_LIBRARY_PATH'] = runtime['libraryPath'] + (':' + env['LD_LIBRARY_PATH'] if env.get('LD_LIBRARY_PATH') else '')
    before = defaults()
    failures = []
    samples = []
    consumption = {'samples': 0, 'nonfinite': 0, 'peak': 0.0}
    processes = []
    result = {'startedAt': datetime.now(timezone.utc).isoformat(), 'requestedSeconds': args.seconds,
              'binarySha256': hashlib.file_digest(args.binary.open('rb'), 'sha256').hexdigest(),
              'runtimeVersions': runtime['versions'], 'inputId': args.input, 'outputId': args.output_device,
              'retainsAudio': False, 'qualityValidated': False, 'jointVllmLoadValidated': False}
    def progress(phase, **fields):
        payload = {'phase': phase, 'observedAt': datetime.now(timezone.utc).isoformat(), **fields}
        temporary = output / 'progress.json.tmp'
        temporary.write_text(json.dumps(payload, indent=2) + '\n')
        temporary.replace(output / 'progress.json')
    started = None
    with tempfile.TemporaryDirectory(prefix='pt-audio-soak-', dir=os.environ['XDG_RUNTIME_DIR']) as directory:
        path = Path(directory) / 'audio.sock'
        with (output / 'engine.log').open('w') as log:
            engine = subprocess.Popen([str(args.binary.resolve()), '--socket', str(path)], env=env, stdout=log, stderr=log)
            processes.append(engine)
            try:
                for _ in range(200):
                    if path.exists():
                        break
                    if engine.poll() is not None:
                        raise RuntimeError('Audio engine exited during startup')
                    time.sleep(.05)

                def command(name, values=None):
                    with socket.socket(socket.AF_UNIX) as client:
                        client.settimeout(10)
                        client.connect(str(path))
                        client.sendall((json.dumps({'id': name, 'command': name, 'args': values or {}}) + '\n').encode())
                        response = json.loads(client.makefile('r').readline())
                        if not response['ok']:
                            raise RuntimeError(response['error'])
                        return response['data']

                (output/'control.json').write_text(json.dumps({'enginePid':engine.pid,'socket':str(path)})+'\n')
                command('configure', {'inputId': args.input, 'outputId': args.output_device,
                                      'aecEnabled': True, 'aecBackend': 'npu',
                                      'npuModelDirectory': runtime['npuModelDirectory'],
                                      'npuRuntimeLibrary': runtime['npuRuntimeLibrary']})
                record = subprocess.Popen(['pw-record', '--target', 'personal-tools-mic', '--rate', '48000',
                                           '--channels', '1', '--format', 'f32', '--raw', '-'],
                                          stdout=subprocess.PIPE, stderr=log)
                processes.append(record)

                def consume():
                    while data := record.stdout.read(8192):
                        values = array.array('f', data)
                        consumption['samples'] += len(values)
                        for value in values:
                            if not math.isfinite(value):
                                consumption['nonfinite'] += 1
                            else:
                                consumption['peak'] = max(consumption['peak'], abs(value))

                consumer = threading.Thread(target=consume, daemon=True)
                consumer.start()
                playback = subprocess.Popen(['pw-play', '--target', 'personal-tools-output', '--rate', '48000',
                                             '--channels', '2', '--format', 'f32', '--raw', '/dev/zero'],
                                            stdout=subprocess.DEVNULL, stderr=log)
                processes.append(playback)
                for _ in range(120):
                    state = command('status')
                    if state['npu']['status'] == 'error':
                        raise RuntimeError(state['npu']['error'])
                    if state['activeBackend'] == 'npu' and not state['degraded'] and consumption['samples'] > 0:
                        break
                    time.sleep(.5)
                else:
                    raise RuntimeError('Real NPU graph did not become active within 60 seconds')
                result['startupStatus']=state
                # A cold first inference and graph activation are recorded above.
                # Begin the sustained interval only after bounded warm-up.
                time.sleep(5)
                # A durable user unit can keep this warm while the independently
                # managed vLLM service starts. The signal begins the measured
                # interval; warm-up never counts as combined-load acceptance.
                if args.start_signal:
                    deadline = time.monotonic() + args.ready_timeout
                    with (output / 'readiness.jsonl').open('w', buffering=1) as records:
                        while not args.start_signal.exists():
                            state = command('status')
                            progress('ready', enginePid=engine.pid, socket=str(path),
                                     activeBackend=state['activeBackend'], degraded=state['degraded'])
                            records.write(json.dumps({'observedAt': datetime.now(timezone.utc).isoformat(),
                                                     'status': state}) + '\n')
                            if engine.poll() is not None or record.poll() is not None or playback.poll() is not None:
                                raise RuntimeError('Audio process exited while awaiting joint-load signal')
                            if time.monotonic() >= deadline:
                                raise RuntimeError('Timed out waiting for joint-load start signal')
                            time.sleep(2)
                    result['startSignal'] = str(args.start_signal.resolve())
                    result['startSignalContent'] = args.start_signal.read_text()[:16384]
                state=command('status')
                # Editing EQ while NPU is running must retain the worker and its counters.
                before_edit = state['npu']['inferences']
                filters = [{'frequency': 1000, 'gain': -3, 'q': 1}]
                state = command('configure', {'leftFilters': filters, 'rightFilters': filters, 'eqEnabled': True})
                if state['npu']['inferences'] < before_edit or state['npu']['status'] != 'ready':
                    raise RuntimeError('EQ editing reset the NPU processor')
                baseline = state
                started = time.monotonic()
                result['measurementStartedAt']=datetime.now(timezone.utc).isoformat()
                with (output / 'samples.jsonl').open('w', buffering=1) as records:
                    while True:
                        elapsed = time.monotonic() - started
                        state = command('status')
                        memory = Path(f'/proc/{engine.pid}/status').read_text()
                        rss = next(int(line.split()[1]) for line in memory.splitlines() if line.startswith('VmRSS:'))
                        sample = {'elapsedSeconds': round(elapsed, 3), 'rssKiB': rss,
                                  'status': state['status'], 'activeBackend': state['activeBackend'],
                                  'degraded': state['degraded'], 'degradedReason':state.get('degradedReason',''), 'npu': state['npu'],
                                  'diagnostics': state['diagnostics'], 'consumption': dict(consumption)}
                        samples.append(sample)
                        records.write(json.dumps(sample) + '\n')
                        progress('measuring', elapsedSeconds=sample['elapsedSeconds'],
                                 enginePid=engine.pid, socket=str(path), activeBackend=state['activeBackend'],
                                 degraded=state['degraded'], nonfinite=consumption['nonfinite'])
                        if elapsed >= args.seconds:
                            break
                        if record.poll() is not None or playback.poll() is not None:
                            raise RuntimeError('PipeWire test stream exited')
                        time.sleep(min(5, args.seconds - elapsed))
                if any(s['activeBackend'] != 'npu' or s['degraded'] for s in samples):
                    failures.append('NPU output was degraded during sampled intervals')
                for key in ('queueDrops', 'unsupportedRateCallbacks', 'oversizedCallbacks'):
                    if state['diagnostics'][key] > baseline['diagnostics'][key]:
                        failures.append(f'Increased {key}')
                for key in ('missingFrames', 'droppedPackets'):
                    if state['npu'][key] > baseline['npu'][key]:
                        failures.append(f'Increased NPU {key}')
                if consumption['nonfinite']:
                    failures.append('Nonfinite output samples')
                if state['npu']['inferences'] <= baseline['npu']['inferences']:
                    failures.append('Inference did not advance')
                result.update({'elapsedSeconds': time.monotonic() - started, 'baseline': baseline,
                               'final': state, 'consumption': dict(consumption),
                               'maximumRssKiB': max(s['rssKiB'] for s in samples),
                               'eqEditPreservedNpu': True})
                command('shutdown')
                engine.wait(timeout=15)
            except (Exception, KeyboardInterrupt) as error:
                failures.append(str(error) or 'Test interrupted')
                (output/'failure.txt').write_text(traceback.format_exc())
            finally:
                for process in reversed(processes):
                    if process.poll() is None:
                        process.terminate()
                        try:
                            process.wait(timeout=15)
                        except subprocess.TimeoutExpired:
                            process.kill()
                            process.wait()
                after = defaults()
                result['defaultsUnchanged'] = before == after
                if before != after:
                    failures.append('Default device metadata changed during the test')
                result['finishedAt'] = datetime.now(timezone.utc).isoformat()
                if started is not None:
                    result['elapsedSeconds'] = time.monotonic() - started
                result['failures'] = failures
                result['ok'] = not failures
                (output / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
                progress('completed' if result['ok'] else 'failed', ok=result['ok'], failures=failures)
    print(json.dumps({k: v for k, v in result.items() if k not in ('baseline', 'final')}, indent=2), flush=True)
    return 0 if result['ok'] else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', type=Path, required=True)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--input', required=True)
    parser.add_argument('--output-device', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--seconds', type=float, default=1800)
    parser.add_argument('--start-signal', type=Path,
                        help='Keep real audio warm until this file exists, then begin the measured interval')
    parser.add_argument('--ready-timeout', type=float, default=7200)
    arguments = parser.parse_args()
    if arguments.seconds <= 0:
        parser.error('--seconds must be positive')
    if arguments.ready_timeout <= 0:
        parser.error('--ready-timeout must be positive')
    raise SystemExit(run(arguments))
