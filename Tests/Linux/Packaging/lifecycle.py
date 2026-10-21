#!/usr/bin/env python3
"""Explicitly execute package upgrade/removal/reinstall acceptance on this host.

Without --execute, only prints the planned commands. Sudo authentication is always
handled by the inherited terminal; no password is read, stored or logged here.
The daemon/audio must stay inactive and the independent vLLM PID/state unchanged.
"""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent
UNITS = ['personal-tools.service', 'personal-tools-audio.service', 'personal-tools-vllm.service']


def now():
    return datetime.now(timezone.utc).isoformat()


def query(*args):
    return subprocess.run(args, capture_output=True, text=True, timeout=20)


def installed_version():
    value = query('dpkg-query', '-W', '-f=${Version}\t${Status}', 'personal-tools')
    version, _, status = value.stdout.partition('\t')
    return version if value.returncode == 0 and status == 'install ok installed' else None


def states():
    result = {}
    for unit in UNITS:
        value = query('systemctl', '--user', 'show', unit, '--property=ActiveState,MainPID', '--no-pager')
        fields = dict(line.split('=', 1) for line in value.stdout.splitlines() if '=' in line)
        result[unit] = fields
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--deb', type=Path)
    parser.add_argument('--execute', action='store_true', help='Perform the explicitly requested real package lifecycle')
    parser.add_argument('--report', type=Path, default=ROOT/'build/linux/package-lifecycle.json')
    args = parser.parse_args()
    package = (args.deb or max((ROOT/'build/linux/packages').glob('personal-tools_*.deb'), key=lambda p: p.stat().st_mtime)).resolve()
    metadata = query('dpkg-deb', '-f', str(package), 'Package', 'Version')
    if metadata.returncode or 'Package: personal-tools\n' not in metadata.stdout:
        raise RuntimeError('Input must be a built personal-tools .deb')
    desired = next(line.removeprefix('Version: ') for line in metadata.stdout.splitlines() if line.startswith('Version: '))
    install = ['sudo', 'apt-get', 'install', '-y', str(package)]
    remove = ['sudo', 'apt-get', 'remove', '-y', 'personal-tools']
    reload = ['systemctl', '--user', 'daemon-reload']
    if not args.execute:
        print('Read-only plan. Add --execute in an interactive terminal to perform these operations:')
        print(json.dumps({'package': str(package), 'targetVersion': desired, 'commands': [install, remove, install, reload],
                          'preservation': 'Fresh before snapshot and comparisons after upgrade, removal and reinstall.',
                          'guards': 'Daemon/audio inactive; independent vLLM state and PID unchanged.',
                          'recovery': 'Attempt reinstall in finally if removal/reinstall fails.'}, ensure_ascii=False, indent=2))
        return 0
    if not sys.stdin.isatty():
        raise RuntimeError('--execute requires an interactive terminal for sudo authentication')
    args.report.parent.mkdir(parents=True, exist_ok=True)
    baseline = args.report.with_name(args.report.stem+'-preservation-before.json')
    report = {'startedAt': now(), 'package': str(package), 'targetVersion': desired,
              'initialVersion': installed_version(), 'steps': [], 'guards': [], 'executed': True,
              'credentialHandling': 'Sudo inherits terminal; this process never reads or records a password.'}
    original = states()
    report['servicesBefore'] = original
    restore_required = False
    passed = False

    def save():
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n')

    def guard(stage):
        current = states()
        safe = all(current[unit].get('ActiveState') == 'inactive' and current[unit].get('MainPID') == '0' for unit in UNITS[:2])
        safe = safe and current[UNITS[2]] == original[UNITS[2]] and bool(original[UNITS[2]])
        report['guards'].append({'stage': stage, 'observedAt': now(), 'passed': safe, 'services': current})
        save()
        if not safe:
            raise RuntimeError(f'Service invariant failed at {stage}; no service will be started or stopped by this script')

    def run(name, command, check=True):
        step = {'name': name, 'command': command, 'startedAt': now()}
        report['steps'].append(step)
        save()
        # No pipes, password arguments, environment password variables or log capture.
        result = subprocess.run(command)
        step.update(returnCode=result.returncode, finishedAt=now())
        save()
        if check and result.returncode:
            raise RuntimeError(f'{name} failed with exit {result.returncode}')
        return result.returncode

    def compare(stage):
        output = args.report.with_name(args.report.stem+f'-preservation-{stage}.json')
        run(f'preservation-{stage}', [sys.executable, str(HERE/'preserve.py'), 'compare', '--baseline', str(baseline), '--report', str(output)])

    try:
        if report['initialVersion'] is None:
            raise RuntimeError('An installed personal-tools package is required for upgrade acceptance')
        if report['initialVersion'] == desired:
            raise RuntimeError('Target version equals installed version; use a newer build to verify a real upgrade')
        comparison = query('dpkg', '--compare-versions', desired, 'gt', report['initialVersion'])
        if comparison.returncode:
            raise RuntimeError('Target version must be newer than the installed package')
        guard('before-snapshot')
        run('preservation-snapshot', [sys.executable, str(HERE/'preserve.py'), 'snapshot', '--baseline', str(baseline)])
        guard('before-upgrade')
        run('upgrade', install)
        if installed_version() != desired:
            raise RuntimeError('Upgrade did not install the requested version')
        guard('after-upgrade')
        compare('upgrade')
        guard('before-remove')
        restore_required = True
        run('remove', remove)
        if installed_version() is not None:
            raise RuntimeError('Package is still installed after remove')
        guard('after-remove')
        compare('remove')
        guard('before-reinstall')
        run('reinstall', install)
        if installed_version() != desired:
            raise RuntimeError('Reinstall did not restore the requested version')
        restore_required = False
        run('daemon-reload', reload)
        guard('after-reinstall')
        compare('reinstall')
        passed = True
    except (Exception, KeyboardInterrupt) as error:
        report['error'] = str(error) or type(error).__name__
    finally:
        if restore_required:
            try:
                report['recoveryAttempted'] = True
                code = run('recovery-reinstall', install, check=False)
                run('recovery-daemon-reload', reload, check=False)
                report['recoverySucceeded'] = code == 0 and installed_version() == desired
            except (Exception, KeyboardInterrupt) as error:
                report['recoverySucceeded'] = False
                report['recoveryError'] = str(error) or type(error).__name__
        report['finalVersion'] = installed_version()
        report['servicesAfter'] = states()
        report['servicesUnchanged'] = report['servicesAfter'] == original
        report['finishedAt'] = now()
        report['passed'] = passed and report['servicesUnchanged'] and report['finalVersion'] == desired
        save()
    print(f"{'PASS' if report['passed'] else 'FAIL'} package lifecycle: {args.report}")
    if report.get('error'):
        print(report['error'])
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
