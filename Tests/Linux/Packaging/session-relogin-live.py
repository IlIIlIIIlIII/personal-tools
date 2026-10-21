#!/usr/bin/env python3
"""Reproduce login dependency behavior using disposable user systemd units.

Only unique runtime targets and /usr/bin/sleep workers are changed. Never starts
or stops the real graphical session or any Personal Tools service.
"""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[3]
PRODUCTION = ['personal-tools.service', 'personal-tools-audio.service', 'personal-tools-vllm.service']


def command(*args, check=True):
    result = subprocess.run(args, text=True, capture_output=True, timeout=20)
    if check and result.returncode:
        raise RuntimeError(f"{' '.join(args)}: {result.stderr.strip()}")
    return result


def status(unit):
    result = command('systemctl', '--user', 'show', unit, '-p', 'ActiveState', '-p', 'MainPID',
                     '-p', 'InvocationID', '-p', 'ActiveEnterTimestampMonotonic')
    return dict(line.split('=', 1) for line in result.stdout.splitlines() if '=' in line)


def await_state(unit, expected):
    for _ in range(100):
        result = status(unit)
        if result['ActiveState'] == expected:
            return result
        time.sleep(0.05)
    raise AssertionError(f'{unit} did not become {expected}: {result}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--execute', action='store_true')
    parser.add_argument('--report', type=Path, default=ROOT/'build/linux/session-relogin-live.json')
    args = parser.parse_args()
    if not args.execute:
        print(__doc__)
        print('Use --execute to start and clean up the isolated runtime test units.')
        return 0
    prefix = 'pt-relogin-qa-'+uuid.uuid4().hex[:12]
    default = prefix+'-default.target'
    graphical = prefix+'-graphical.target'
    old, new = prefix+'-legacy.service', prefix+'-fixed.service'
    units = [default, graphical, old, new]
    runtime = Path(os.environ['XDG_RUNTIME_DIR'])/'systemd/user'
    runtime.mkdir(parents=True, exist_ok=True)
    before = {unit: status(unit) for unit in PRODUCTION}
    real_targets_before = {unit: status(unit) for unit in ['default.target', 'graphical-session.target']}
    report = {'observedAt': datetime.now(timezone.utc).isoformat(), 'prefix': prefix,
              'scope': 'Real user systemd manager; unique disposable targets and sleep workers only.',
              'productionBefore': before, 'realTargetsBefore': real_targets_before, 'checks': []}

    def check(name, passed, details=None):
        report['checks'].append({'name': name, 'passed': bool(passed), 'details': details})
        if not passed:
            raise AssertionError(name)

    try:
        for unit in [default, graphical]:
            (runtime/unit).write_text(f'[Unit]\nDescription=Personal Tools disposable relogin test {unit}\n')
        for worker, target in [(old, default), (new, graphical)]:
            (runtime/worker).write_text(
                f'[Unit]\nDescription=Personal Tools disposable relogin worker\n'
                f'After={graphical}\nPartOf={graphical}\n\n'
                '[Service]\nType=simple\nExecStart=/usr/bin/sleep 300\n\n'
                f'[Install]\nWantedBy={target}\n')
        command('systemctl', '--user', 'daemon-reload')
        command('systemctl', '--user', '--runtime', 'enable', old, new)
        command('systemctl', '--user', 'start', default, graphical)
        first_old, first_new = await_state(old, 'active'), await_state(new, 'active')
        default_first = status(default)
        check('initial-login-starts-both-workers', int(first_old['MainPID']) > 0
              and int(first_new['MainPID']) > 0, {'legacy': first_old, 'fixed': first_new})
        command('systemctl', '--user', 'stop', graphical)
        stopped_old, stopped_new = await_state(old, 'inactive'), await_state(new, 'inactive')
        check('logout-stops-both-partof-workers', stopped_old['MainPID'] == '0' and stopped_new['MainPID'] == '0')
        check('default-like-target-remains-active', status(default) == default_first)
        command('systemctl', '--user', 'start', graphical)
        second_new = await_state(new, 'active')
        check('legacy-worker-misses-next-login', status(old)['ActiveState'] == 'inactive'
              and status(old)['MainPID'] == '0')
        check('graphical-wanted-worker-restarts-with-new-pid', int(second_new['MainPID']) > 0
              and second_new['MainPID'] != first_new['MainPID']
              and second_new['InvocationID'] != first_new['InvocationID'], second_new)
        check('default-like-target-was-never-restarted', status(default) == default_first)
    except Exception as error:
        report['error'] = str(error)
    finally:
        cleanup = []
        for args_ in [('stop', *units), ('--runtime', 'disable', old, new)]:
            result = command('systemctl', '--user', *args_, check=False)
            if result.returncode:
                cleanup.append(result.stderr.strip())
        for unit in units:
            (runtime/unit).unlink(missing_ok=True)
        for target in [default, graphical]:
            folder = runtime/(target+'.wants')
            if folder.exists():
                for worker in [old, new]:
                    (folder/worker).unlink(missing_ok=True)
                folder.rmdir()
        command('systemctl', '--user', 'daemon-reload')
        command('systemctl', '--user', 'reset-failed', *units, check=False)
        after = {unit: status(unit) for unit in PRODUCTION}
        targets_after = {unit: status(unit) for unit in ['default.target', 'graphical-session.target']}
        report['productionAfter'] = after
        report['realTargetsAfter'] = targets_after
        report['checks'].append({'name': 'production-services-and-pids-unchanged', 'passed': before == after})
        report['checks'].append({'name': 'real-login-targets-never-restarted', 'passed': real_targets_before == targets_after})
        report['checks'].append({'name': 'temporary-runtime-units-removed',
                                 'passed': not list(runtime.glob(prefix+'*')) and not cleanup, 'details': cleanup})
        report['passed'] = 'error' not in report and all(item['passed'] for item in report['checks'])
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n')
    for item in report['checks']:
        print(f"{'PASS' if item['passed'] else 'FAIL'} {item['name']}")
    if 'error' in report:
        print(report['error'])
    print(args.report)
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
