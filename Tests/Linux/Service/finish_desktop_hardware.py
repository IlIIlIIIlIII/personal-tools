#!/usr/bin/env python3
"""Finish reversible desktop checks after the durable audio/model load window."""
import datetime
import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location('runtime', ROOT / 'Sources/Linux/Vllm/runtime.py')
runtime = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime)
OUTPUT = ROOT / 'build/linux/desktop-qa/finish-result.json'


def active(unit):
    return subprocess.run(['systemctl', '--user', 'is-active', '--quiet', unit]).returncode == 0


def save(report):
    runtime.atomic_json(OUTPUT, {**report, 'observedAt': datetime.datetime.now(datetime.timezone.utc).isoformat()})


def main():
    report = {'phase': 'waiting-for-joint-window', 'passed': False}
    save(report)
    deadline = time.monotonic()+5400
    while any(active(unit) for unit in ['personal-tools-audio-acceptance.service', 'personal-tools-audio-acceptance-v2.service', 'personal-tools-vllm-acceptance.service']):
        if time.monotonic() > deadline:
            report['error'] = 'Joint-load units did not finish within 90 minutes'
            save(report)
            return 1
        time.sleep(3)
    restore = active('personal-tools-vllm.service')
    report['vllmWasActive'] = restore
    try:
        # HDR reconfiguration must not turn a completed memory-pressure test into
        # an unrelated CUDA failure. Return the independently owned model to its
        # exact previous running/stopped lifecycle state afterwards.
        if restore:
            subprocess.run(['systemctl', '--user', 'stop', 'personal-tools-vllm.service'], check=True, timeout=45)
        report['phase'] = 'hdr-tests'
        save(report)
        environment = dict(os.environ, PERSONALTOOLS_DESKTOP_REPORT=str(OUTPUT.with_name('hdr-result.json')))
        subprocess.run([str(ROOT / 'build/linux/toolchain/dotnet/dotnet'), str(ROOT / 'Tests/Linux/Service/bin/Debug/net10.0/PersonalTools.Service.Tests.dll'), '--live-desktop', '--exercise-hdr'], cwd=ROOT, env=environment, check=True, timeout=240)
        report['hdrReport'] = str(OUTPUT.with_name('hdr-result.json'))
        report['passed'] = json.loads(OUTPUT.with_name('hdr-result.json').read_text())['passed']
    except Exception as error:
        report['error'] = str(error)
    finally:
        restore_file = OUTPUT.with_name('hdr-restore.json')
        if report.get('error') and restore_file.exists():
            try:
                subprocess.run([str(ROOT / 'build/linux/toolchain/dotnet/dotnet'), str(ROOT / 'Tests/Linux/Service/bin/Debug/net10.0/PersonalTools.Service.Tests.dll'), '--restore-hdr', str(restore_file)], cwd=ROOT, check=True, timeout=120)
                report['hdrRecovered'] = True
            except Exception as error:
                report['hdrRestoreError'] = str(error)
        if restore:
            report['phase'] = 'restoring-vllm'
            save(report)
            try:
                subprocess.run(['systemctl', '--user', 'start', 'personal-tools-vllm.service'], check=True, timeout=45)
                deadline = time.monotonic()+1200
                while time.monotonic() < deadline:
                    state = json.loads((runtime.STATE / 'status.json').read_text())
                    if state.get('status') == 'running' and runtime.model_ready():
                        report['vllmRestored'] = True
                        break
                    if not active('personal-tools-vllm.service'):
                        raise RuntimeError(state.get('error') or 'vLLM failed to restart')
                    time.sleep(2)
                else:
                    raise RuntimeError('vLLM restore readiness timeout')
            except Exception as error:
                report['passed'] = False
                report['restoreError'] = str(error)
        report['phase'] = 'complete' if report['passed'] else 'failed'
        save(report)
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    sys.exit(main())
