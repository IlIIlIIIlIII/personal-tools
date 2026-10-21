#!/usr/bin/env python3
"""Verify engine dependency recovery with disposable user systemd workers.

Only unique runtime service files and /usr/bin/sleep workers are changed. The
production audio, main service, vLLM and graphical targets are never restarted.
"""
import argparse
import configparser
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[3]
PRODUCTION = ['personal-tools.service', 'personal-tools-audio.service', 'personal-tools-vllm.service',
              'graphical-session.target', 'default.target']


def command(*args, check=True):
    result = subprocess.run(args, text=True, capture_output=True, timeout=20)
    if check and result.returncode:
        raise RuntimeError(f"{' '.join(args)}: {result.stderr.strip()}")
    return result


def status(unit):
    result = command('systemctl', '--user', 'show', unit, '-p', 'ActiveState', '-p', 'MainPID', '-p', 'InvocationID')
    return dict(line.split('=', 1) for line in result.stdout.splitlines() if '=' in line)


def wait_for(predicate, message):
    for _ in range(100):
        if predicate():
            return
        time.sleep(.05)
    raise AssertionError(message)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--execute', action='store_true')
    parser.add_argument('--report', type=Path, default=ROOT/'build/linux/audio-service-lifecycle.json')
    args = parser.parse_args()
    if not args.execute:
        print(__doc__)
        return 0
    prefix = 'pt-audio-lifecycle-qa-'+uuid.uuid4().hex[:12]
    workers = [(prefix+'-'+kind+'-main.service', prefix+'-'+kind+'-engine.service') for kind in ['legacy', 'fixed']]
    units = [unit for pair in workers for unit in pair]
    runtime = Path(os.environ['XDG_RUNTIME_DIR'])/'systemd/user'
    runtime.mkdir(parents=True, exist_ok=True)
    configuration = configparser.ConfigParser(interpolation=None)
    configuration.read(ROOT/'Sources/Linux/Packaging/personal-tools-audio.service')
    before = {unit: status(unit) for unit in PRODUCTION}
    report = {'observedAt': datetime.now(timezone.utc).isoformat(), 'scope': __doc__, 'productionBefore': before, 'checks': []}

    def check(name, value):
        report['checks'].append({'name': name, 'passed': bool(value)})
        if not value:
            raise AssertionError(name)

    try:
        for fixed, (main, engine) in enumerate(workers):
            (runtime/main).write_text(f'[Unit]\nDescription=Disposable audio owner test\nWants={engine}\nAfter={engine}\n'
                                     '[Service]\nType=simple\nExecStart=/usr/bin/sleep 300\n')
            # SIGTERM is a clean result for sleep, just like the engine's
            # protocol shutdown exit0; a normal exit must not orphan the UI.
            policy = f"StopWhenUnneeded={configuration['Unit'].get('StopWhenUnneeded', 'no')}\n" if fixed else ''
            restart = configuration['Service']['Restart'] if fixed else 'on-failure'
            (runtime/engine).write_text(f'[Unit]\nDescription=Disposable audio engine test\nPartOf={main}\n{policy}'
                                       f'[Service]\nType=simple\nExecStart=/usr/bin/sleep 300\nRestart={restart}\nRestartSec=.1\n')
        command('systemctl', '--user', 'daemon-reload')
        for main, engine in workers:
            command('systemctl', '--user', 'start', main)
            wait_for(lambda: status(engine)['ActiveState'] == 'active', engine+' startup')
        original = [status(engine) for _, engine in workers]
        for _, engine in workers:
            command('systemctl', '--user', 'kill', '--signal=SIGTERM', '--kill-whom=main', engine)
        legacy_main, legacy_engine = workers[0]
        fixed_main, fixed_engine = workers[1]
        wait_for(lambda: status(legacy_engine)['ActiveState'] == 'inactive', 'legacy clean exit')
        check('legacy-clean-engine-exit-leaves-running-owner-without-audio', status(legacy_main)['ActiveState'] == 'active')
        wait_for(lambda: status(fixed_engine)['ActiveState'] == 'active' and status(fixed_engine)['MainPID'] != original[1]['MainPID'], 'fixed engine restart')
        check('fixed-clean-engine-exit-recovers-while-owner-active', status(fixed_main)['ActiveState'] == 'active')
        command('systemctl', '--user', 'stop', fixed_main)
        wait_for(lambda: status(fixed_engine)['ActiveState'] == 'inactive', 'PartOf stop')
        time.sleep(.3)
        check('explicit-owner-stop-keeps-engine-stopped', status(fixed_main)['ActiveState'] == 'inactive' and status(fixed_engine)['ActiveState'] == 'inactive')
        command('systemctl', '--user', 'start', fixed_main)
        wait_for(lambda: status(fixed_engine)['ActiveState'] == 'active', 'second startup')
        previous_main, previous_engine = status(fixed_main), status(fixed_engine)
        command('systemctl', '--user', 'restart', fixed_main)
        wait_for(lambda: status(fixed_main)['ActiveState'] == 'active' and status(fixed_engine)['ActiveState'] == 'active', 'owner restart')
        check('owner-restart-restarts-dependent-engine', status(fixed_main)['MainPID'] != previous_main['MainPID'] and status(fixed_engine)['MainPID'] != previous_engine['MainPID'])
        # Simulate rapid activation while an old engine clean shutdown is in
        # flight: start sees active, then the engine exits. Recovery is required.
        command('systemctl', '--user', 'start', fixed_main)
        previous_engine = status(fixed_engine)
        command('systemctl', '--user', 'kill', '--signal=SIGTERM', '--kill-whom=main', fixed_engine)
        command('systemctl', '--user', 'start', fixed_main)
        wait_for(lambda: status(fixed_engine)['ActiveState'] == 'active' and status(fixed_engine)['MainPID'] != previous_engine['MainPID'], 'rapid activation recovery')
        check('rapid-owner-activation-does-not-leave-audio-inactive', status(fixed_main)['ActiveState'] == 'active')
        # UI Quit exits the owner process, without a systemctl stop transaction.
        command('systemctl', '--user', 'kill', '--signal=SIGTERM', '--kill-whom=main', fixed_main)
        wait_for(lambda: status(fixed_main)['ActiveState'] == 'inactive' and status(fixed_engine)['ActiveState'] == 'inactive', 'unneeded engine stop')
        time.sleep(.3)
        check('owner-natural-exit-stops-unneeded-engine-without-respawn', status(fixed_engine)['ActiveState'] == 'inactive')
    except Exception as error:
        report['error'] = str(error)
    finally:
        command('systemctl', '--user', 'stop', *units, check=False)
        for unit in units:
            (runtime/unit).unlink(missing_ok=True)
        command('systemctl', '--user', 'daemon-reload')
        command('systemctl', '--user', 'reset-failed', *units, check=False)
        after = {unit: status(unit) for unit in PRODUCTION}
        report['productionAfter'] = after
        report['checks'].append({'name': 'production-services-and-targets-unchanged', 'passed': before == after})
        report['checks'].append({'name': 'disposable-units-removed', 'passed': not list(runtime.glob(prefix+'*'))})
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
