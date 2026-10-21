#!/usr/bin/env python3
"""Exercise default-route ownership on a private PipeWire/WirePlumber session.

WirePlumber's built-in policy-only profile loads no ALSA, Bluetooth, or camera
hardware monitors. Both the PipeWire socket and session bus are private.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time


def run(dotnet, assembly):
    processes=[]
    with tempfile.TemporaryDirectory(prefix='pt-route-private-') as directory:
        root=Path(directory);env=os.environ.copy()
        env.update(XDG_RUNTIME_DIR=directory,PIPEWIRE_RUNTIME_DIR=directory,PIPEWIRE_REMOTE='pipewire-0',
                   XDG_CONFIG_HOME=str(root/'config'),XDG_DATA_HOME=str(root/'data'),
                   XDG_STATE_HOME=str(root/'state'),XDG_CACHE_HOME=str(root/'cache'),
                   PERSONALTOOLS_PRIVATE_AUDIO_TEST=directory)
        config=root/'pipewire.conf'
        config.write_text(Path('/usr/share/pipewire/pipewire.conf').read_text().replace(
            '#audiotestsrc   = audiotestsrc/libspa-audiotestsrc','audiotestsrc   = audiotestsrc/libspa-audiotestsrc'))
        with (root/'session.log').open('w') as log:
            def start(args):
                process=subprocess.Popen(args,env=env,stdout=log,stderr=log);processes.append(process);return process
            def command(*args):
                return subprocess.check_output(args,env=env,text=True,timeout=10)
            try:
                start(['pipewire','-c',str(config)])
                for _ in range(100):
                    if (root/'pipewire-0').exists():break
                    time.sleep(.05)
                start(['wireplumber','--profile','policy'])
                for name,media,factory,priority in [('speakers','Audio/Sink','support.null-audio-sink',2000),
                    ('personal-tools-output','Audio/Sink','support.null-audio-sink',1000),
                    ('physical-mic','Audio/Source','audiotestsrc',2000),
                    ('personal-tools-mic','Audio/Source','audiotestsrc',1000)]:
                    command('pw-cli','create-node','adapter','{ factory.name='+factory+' node.name='+name+
                        ' media.class='+media+' audio.channels=2 audio.position=[ FL FR ] object.linger=true '
                        'priority.session='+str(priority)+' adapter.auto-port-config={ mode=dsp position=preserve } }')
                for _ in range(150):
                    graph=json.loads(command('pw-dump'))
                    metadata=[row for item in graph if item.get('props',{}).get('metadata.name')=='default' for row in item.get('metadata',[])]
                    if any(row.get('key')=='default.audio.source' for row in metadata) and any(row.get('key')=='default.audio.sink' for row in metadata):break
                    time.sleep(.05)
                result=subprocess.run([str(dotnet.resolve()),str(assembly.resolve()),'--private-live'],env=env,text=True,capture_output=True,timeout=40)
                print(result.stdout,end='');print(result.stderr,end='',file=sys.stderr)
                if result.returncode:
                    log.flush();print((root/'session.log').read_text()[-8000:],file=sys.stderr)
                return result.returncode
            finally:
                for process in reversed(processes):
                    if process.poll() is None:
                        process.terminate()
                        try:process.wait(timeout=5)
                        except subprocess.TimeoutExpired:process.kill();process.wait()


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet',type=Path,required=True)
    parser.add_argument('--assembly',type=Path,required=True)
    parser.add_argument('--inside',action='store_true')
    args=parser.parse_args()
    if args.inside:
        raise SystemExit(run(args.dotnet,args.assembly))
    raise SystemExit(subprocess.run(['dbus-run-session','--',sys.executable,__file__,*sys.argv[1:],'--inside']).returncode)
