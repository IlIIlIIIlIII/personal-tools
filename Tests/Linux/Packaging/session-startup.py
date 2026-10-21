#!/usr/bin/env python3
"""Verify login links and upgrade migration without changing the live session.

The setup command runs with adapters that direct systemctl's actual install-link
operations into a temporary root. No live start/stop/reload/extension operation
is allowed. This checks migration and systemd's transaction ordering, not a real
logout/login; the latter must be recorded separately.
"""
import argparse
import configparser
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[3]
UNIT = 'personal-tools.service'
SERVICES = [UNIT, 'personal-tools-audio.service', 'personal-tools-vllm.service']


def command(*args, env=None):
    return subprocess.run(args, env=env, text=True, capture_output=True, timeout=30)


def states():
    result = {}
    for name in SERVICES:
        process = command('systemctl', '--user', 'show', name, '-p', 'ActiveState', '-p', 'MainPID')
        if process.returncode:
            raise RuntimeError(f'Cannot guard live {name}: {process.stderr.strip()}')
        result[name] = process.stdout.strip()
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', type=Path, default=ROOT/'build/linux/session-startup-qa.json')
    args = parser.parse_args()
    report = {'observedAt': datetime.now(timezone.utc).isoformat(), 'checks': [],
              'scope': 'Offline install-link migration and transaction validation; no live logout/login.'}
    before = states()
    report['servicesBefore'] = before

    def check(name, passed, details=None):
        report['checks'].append({'name': name, 'passed': bool(passed), 'details': details})
        if not passed:
            raise AssertionError(name)

    try:
        with tempfile.TemporaryDirectory(prefix='pt-session-startup-') as directory:
            temporary = Path(directory)
            units = temporary/'root/usr/lib/systemd/user'
            units.mkdir(parents=True)
            unit = units/UNIT
            shutil.copyfile(ROOT/'Sources/Linux/Packaging'/UNIT, unit)
            shutil.copyfile(ROOT/'Sources/Linux/Packaging/personal-tools-audio.service', units/'personal-tools-audio.service')
            configuration = configparser.ConfigParser(interpolation=None)
            configuration.read(unit)
            check('each-graphical-session-pulls-in-main', configuration['Install']['WantedBy'].split() == ['graphical-session.target'])
            check('logout-stops-owned-main-and-audio', configuration['Unit']['PartOf'] == 'graphical-session.target'
                  and 'personal-tools-audio.service' in configuration['Unit']['Wants'].split())

            # Seed the actual legacy symlink left behind by the old package.
            legacy = temporary/'root/etc/systemd/user/default.target.wants'/UNIT
            legacy.parent.mkdir(parents=True)
            legacy.symlink_to('/usr/lib/systemd/user/'+UNIT)
            current = temporary/'root/etc/systemd/user/graphical-session.target.wants'/UNIT
            (units/'graphical-session.target').write_text('[Unit]\nDescription=Offline graphical session\n')
            adapters = temporary/'bin'
            adapters.mkdir()
            log = temporary/'commands.jsonl'
            real_systemctl = shutil.which('systemctl')
            adapter = '''#!/usr/bin/env python3
import json, os, subprocess, sys
from pathlib import Path
args = sys.argv[1:]
with Path(os.environ['PT_SETUP_LOG']).open('a') as stream:
    stream.write(json.dumps([Path(sys.argv[0]).name, *args])+'\\n')
if Path(sys.argv[0]).name == 'gnome-extensions':
    assert args == ['enable', 'personal-tools@griplabs.io']
    raise SystemExit(int(os.environ.get('PT_GNOME_RESULT', '0')))
if args == ['--user', 'daemon-reload']:
    raise SystemExit(0)
assert args == ['--user', 'reenable', 'personal-tools.service'], args
raise SystemExit(subprocess.run([os.environ['PT_REAL_SYSTEMCTL'], '--global', '--root='+os.environ['PT_OFFLINE_ROOT'], *args[1:]]).returncode)
'''
            for name in ['systemctl', 'gnome-extensions']:
                path = adapters/name
                path.write_text(adapter)
                path.chmod(0o755)
            environment = {**os.environ, 'PATH': str(adapters)+os.pathsep+os.environ['PATH'],
                           'PT_SETUP_LOG': str(log), 'PT_REAL_SYSTEMCTL': real_systemctl,
                           'PT_OFFLINE_ROOT': str(temporary/'root')}
            setup = str(ROOT/'Sources/Linux/Packaging/personal-tools-setup')
            result = command('python3', setup, '--enable', env=environment)
            check('setup-enable-migrates-legacy-link', result.returncode == 0 and current.is_symlink()
                  and not legacy.is_symlink(), result.stderr.strip())
            result = command('python3', setup, '--enable', env=environment)
            check('setup-enable-is-repeatable', result.returncode == 0 and current.is_symlink()
                  and not legacy.is_symlink())
            result = command('python3', setup, '--enable', env={**environment, 'PT_GNOME_RESULT': '1'})
            check('unloaded-extension-gives-relogin-guidance', result.returncode == 0
                  and 'Log out and back in' in result.stdout and current.is_symlink())
            operations = [json.loads(line) for line in log.read_text().splitlines()]
            check('enable-never-starts-stops-or-restarts-services', len(operations) == 9
                  and not any(token in {'start', 'stop', 'restart', '--now'} for op in operations for token in op), operations)

            # Follow the installed link in an offline systemd transaction. Keep
            # normal default dependencies, so this catches ordering cycles.
            wants = units/'graphical-session.target.wants'
            wants.mkdir()
            (wants/UNIT).symlink_to('../'+UNIT)
            result = command('systemd-analyze', '--user', '--generators=no', '--man=no', 'verify',
                             str(units/'graphical-session.target'),
                             env={**os.environ, 'SYSTEMD_UNIT_PATH': str(units)+':', 'SYSTEMD_LOG_LEVEL': 'warning'})
            check('graphical-session-start-transaction-has-no-cycle', result.returncode == 0,
                  result.stderr.strip())
    except Exception as error:
        report['error'] = str(error)
    finally:
        after = states()
        report['servicesAfter'] = after
        report['checks'].append({'name': 'live-service-states-and-pids-unchanged', 'passed': after == before})
        report['passed'] = 'error' not in report and all(item['passed'] for item in report['checks'])
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n')
    for item in report['checks']:
        print(f"{'PASS' if item['passed'] else 'FAIL'} {item['name']}")
    if report.get('error'):
        print(report['error'])
    print(args.report)
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
