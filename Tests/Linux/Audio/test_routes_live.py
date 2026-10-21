#!/usr/bin/env python3
"""Short live route check after the long-running audio acceptance has stopped.

Creates a temporary, neutral-EQ engine. Uses explicit physical devices and the
daemon's real route owner, then verifies the original default metadata returns.
Does not record audio or alter stored application settings.
"""
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time


def defaults():
    graph=json.loads(subprocess.check_output(['pw-dump'],text=True))
    return {f"{entry['subject']}:{entry['key']}":entry for item in graph
            if item.get('props',{}).get('metadata.name')=='default' for entry in item.get('metadata',[])}


def run(args):
    units=json.loads(subprocess.check_output(['systemctl','--user','list-units',
        '--all','--type=service','--output=json','--no-pager','personal-tools-audio-acceptance*.service'],text=True))
    if any(unit.get('active') not in ('inactive','failed') for unit in units):
        raise RuntimeError('Audio acceptance is still active; refusing to touch defaults')
    before=defaults();process=None;result={'ok':False,'before':before,'recordsAudio':False}
    with tempfile.TemporaryDirectory(prefix='pt-live-routes-',dir=os.environ['XDG_RUNTIME_DIR']) as directory:
        root=Path(directory);path=root/'audio.sock';env=os.environ.copy();env['PERSONALTOOLS_DESKTOP_ROUTE_TEST']=directory
        def command(name,values=None):
            with socket.socket(socket.AF_UNIX) as client:
                client.settimeout(10);client.connect(str(path))
                client.sendall((json.dumps({'id':name,'command':name,'args':values or {}})+'\n').encode())
                reply=json.loads(client.makefile().readline())
                if not reply['ok']:raise RuntimeError(reply['error'])
                return reply['data']
        try:
            with (root/'engine.log').open('w') as log:
                process=subprocess.Popen([str(args.binary.resolve()),'--socket',str(path)],stdout=log,stderr=log)
                for _ in range(100):
                    if path.exists():break
                    if process.poll() is not None:raise RuntimeError((root/'engine.log').read_text())
                    time.sleep(.05)
                neutral=[{'frequency':1000,'gain':0,'q':1}]
                command('configure',{'inputId':args.input,'outputId':args.output_device,'leftFilters':neutral,'rightFilters':neutral,'eqEnabled':True})
                for _ in range(100):
                    if command('status')['captureConnected']:break
                    time.sleep(.05)
                else:raise RuntimeError('Physical microphone did not connect')
                test=subprocess.run([str(args.dotnet.resolve()),str(args.assembly.resolve()),'--desktop-live'],env=env,text=True,capture_output=True,timeout=40)
                result['checks']=test.stdout;result['stderr']=test.stderr
                if test.returncode:raise RuntimeError(test.stdout+test.stderr)
                command('shutdown');process.wait(timeout=10);process=None
                after=defaults();result['after']=after;result['defaultsUnchanged']=before==after
                if before!=after:raise RuntimeError('Original default metadata did not return exactly')
                result['ok']=True
        except Exception as error:
            result['error']=str(error)
            journal=root/'routes.json'
            if journal.exists():result['pendingRouteJournal']=json.loads(journal.read_text())
        finally:
            if process is not None and process.poll() is None:
                process.terminate()
                try:process.wait(timeout=10)
                except subprocess.TimeoutExpired:process.kill();process.wait()
    args.report.parent.mkdir(parents=True,exist_ok=True);args.report.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result,indent=2));return 0 if result['ok'] else 1


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    for flag in ['binary','dotnet','assembly','report']:parser.add_argument('--'+flag,type=Path,required=True)
    parser.add_argument('--input',required=True);parser.add_argument('--output-device',required=True)
    raise SystemExit(run(parser.parse_args()))
