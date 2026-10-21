#!/usr/bin/env python3
"""Launch the installed self-contained service on a private bus and XDG tree.

Never starts a systemd/audio/vLLM service. Checks actual apphost execution,
GetSnapshot, settings writes/reload and clean lifecycle quit. Hardware routes
remain unreachable through the private runtime/socket/session-bus paths.
"""
import argparse
import ast
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[3]
BUS = 'io.griplabs.PersonalTools'
PATH = '/io/griplabs/PersonalTools'
UNITS = ['personal-tools.service', 'personal-tools-audio.service', 'personal-tools-vllm.service']


def real_service_states():
    return {unit: subprocess.run(['systemctl', '--user', 'show', unit, '--property=ActiveState,MainPID', '--no-pager'],
                                capture_output=True, text=True, timeout=10).stdout.strip() for unit in UNITS}


def dbus(method, *args):
    value = subprocess.run(['gdbus', 'call', '--session', '--dest', BUS, '--object-path', PATH,
                            '--method', f'io.griplabs.PersonalTools1.{method}', *args],
                           capture_output=True, text=True, timeout=25)
    if value.returncode:
        raise RuntimeError(f'{method} failed: {value.stderr.strip()}')
    return json.loads(ast.literal_eval(value.stdout.strip())[0])


def owner():
    value = subprocess.run(['gdbus', 'call', '--session', '--dest', 'org.freedesktop.DBus',
                            '--object-path', '/org/freedesktop/DBus', '--method', 'org.freedesktop.DBus.NameHasOwner', BUS],
                           capture_output=True, text=True, timeout=5)
    return value.returncode == 0 and value.stdout.strip() == '(true,)'


def wait_owner(process, expected):
    deadline = time.monotonic()+45
    while time.monotonic() < deadline:
        if owner() == expected:
            return
        if expected and process.poll() is not None:
            raise RuntimeError(f'Installed apphost exited before owning D-Bus: {process.returncode}')
        time.sleep(0.1)
    raise RuntimeError(f'D-Bus owner did not become {expected}')


def worker(binary, output):
    evidence = {'observedAt': datetime.now(timezone.utc).isoformat(), 'binary': str(binary), 'checks': []}
    def check(name, condition):
        evidence['checks'].append({'name': name, 'passed': bool(condition)})
        if not condition:
            raise RuntimeError(name)
    log = output.with_suffix('.log')
    fd = os.open(log, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    process = None
    try:
        with os.fdopen(fd, 'w') as stream:
            process = subprocess.Popen([str(binary)], stdout=stream, stderr=subprocess.STDOUT)
            wait_owner(process, True)
            initial = dbus('GetSnapshot')
            check('installed-apphost-snapshot', initial.get('version') == 1 and initial['service']['status'] == 'running')
            check('empty-private-archive', initial['clipboard']['entryCount'] == 0)
            check('private-data-path', initial['settings']['dataPath'] == str(Path(os.environ['XDG_DATA_HOME'])/'personal-tools'))
            for theme in ['dark', 'light']:
                result = dbus('Execute', 'settings', 'update', json.dumps({'theme': theme}))
                check(f'settings-{theme}-command', result.get('ok'))
                check(f'settings-{theme}-roundtrip', dbus('GetSnapshot')['settings']['theme'] == theme)
            persisted = Path(os.environ['XDG_CONFIG_HOME'])/'personal-tools/settings.json'
            check('settings-private-file', persisted.is_file() and persisted.stat().st_mode & 0o777 == 0o600)
            result = dbus('Execute', 'lifecycle', 'quit', '{}')
            check('lifecycle-quit-command', result.get('ok'))
            process.wait(timeout=30)
            check('clean-process-exit', process.returncode == 0)
            wait_owner(process, False)
            check('dbus-name-released', not owner())
            process = subprocess.Popen([str(binary)], stdout=stream, stderr=subprocess.STDOUT)
            wait_owner(process, True)
            check('settings-survive-apphost-restart', dbus('GetSnapshot')['settings']['theme'] == 'light')
            result = dbus('Execute', 'lifecycle', 'quit', '{}')
            check('second-lifecycle-quit-command', result.get('ok'))
            process.wait(timeout=30)
            check('second-clean-process-exit', process.returncode == 0)
            check('no-audio-runtime-created', not (Path(os.environ['XDG_RUNTIME_DIR'])/'personal-tools/audio.sock').exists())
            check('no-vllm-runtime-created', not (Path(os.environ['XDG_DATA_HOME'])/'personal-tools/vllm').exists())
    except Exception as error:
        evidence['error'] = str(error)
    finally:
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)
        evidence['passed'] = 'error' not in evidence and all(item['passed'] for item in evidence['checks'])
        output.write_text(json.dumps(evidence, ensure_ascii=False, indent=2)+'\n')
    return 0 if evidence['passed'] else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', type=Path, default=Path('/usr/lib/personal-tools/service/personal-tools-service'))
    parser.add_argument('--output', type=Path, default=ROOT/'build/linux/installed-service-smoke.json')
    parser.add_argument('--worker', action='store_true', help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.worker:
        return worker(args.binary, args.output)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    before = real_service_states()
    if any('ActiveState=inactive' not in before[unit] for unit in UNITS[:2]):
        raise RuntimeError('Personal Tools daemon/audio must already be inactive. This test never stops them.')
    with tempfile.TemporaryDirectory(prefix='personal-tools-installed-smoke-') as private:
        env = {key: value for key, value in os.environ.items()
               if not key.startswith(('DOTNET_', 'PERSONALTOOLS_')) and key not in {
                   'DBUS_SESSION_BUS_ADDRESS', 'WAYLAND_DISPLAY', 'DISPLAY', 'PULSE_SERVER',
                   'PIPEWIRE_REMOTE', 'PIPEWIRE_RUNTIME_DIR', 'LD_LIBRARY_PATH', 'LD_PRELOAD'}}
        for name in ['CONFIG', 'DATA', 'STATE', 'CACHE', 'RUNTIME']:
            folder = Path(private)/name.lower()
            folder.mkdir(mode=0o700)
            env[f'XDG_{name}_HOME' if name != 'RUNTIME' else 'XDG_RUNTIME_DIR'] = str(folder)
        result = subprocess.run(['dbus-run-session', '--', sys.executable, str(Path(__file__).resolve()), '--worker',
                                 '--binary', str(args.binary.resolve()), '--output', str(args.output.resolve())],
                                env=env, capture_output=True, text=True, timeout=150)
    report = json.loads(args.output.read_text()) if args.output.exists() else {'passed': False, 'error': 'Worker produced no report'}
    after = real_service_states()
    report['productionServicesBefore'] = before
    report['productionServicesAfter'] = after
    report['productionServicesUnchanged'] = before == after
    with args.binary.open('rb') as stream:
        report['apphostSha256'] = hashlib.file_digest(stream, 'sha256').hexdigest()
    report['runtimeArtifacts'] = {}
    for name in ['personal-tools-service.dll', 'personal-tools-service.runtimeconfig.json', 'libcoreclr.so']:
        artifact = args.binary.parent/name
        if artifact.is_file():
            with artifact.open('rb') as stream:
                report['runtimeArtifacts'][name] = hashlib.file_digest(stream, 'sha256').hexdigest()
    report['isolation'] = 'Private session D-Bus, runtime and XDG config/data/state/cache; no audio/vLLM/systemd start commands; no inherited DOTNET or library overrides.'
    report['passed'] = report['passed'] and before == after and result.returncode == 0
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n')
    print(f"{'PASS' if report['passed'] else 'FAIL'} installed self-contained service smoke: {args.output}")
    if 'error' in report:
        print(report['error'])
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
