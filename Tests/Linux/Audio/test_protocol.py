#!/usr/bin/env python3
"""Native socket integration checks against an isolated PipeWire server.

Creates temporary virtual endpoints only. Does not select physical devices,
change audio defaults, record samples, or change microphone mute state.
"""
import argparse
import json
import os
from pathlib import Path
import socket
import stat
import subprocess
import tempfile
import time


def run(binary):
    with tempfile.TemporaryDirectory(prefix='personal-tools-audio-test-') as directory:
        path=Path(directory)/'audio.sock'
        env=os.environ.copy()
        env.update(XDG_RUNTIME_DIR=directory,PIPEWIRE_RUNTIME_DIR=directory,PIPEWIRE_REMOTE='pipewire-0')
        env.pop('DBUS_SESSION_BUS_ADDRESS',None)
        server=subprocess.Popen(['pipewire','-c','/usr/share/pipewire/pipewire.conf'],env=env,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        process=None
        try:
            for _ in range(100):
                if (Path(directory)/'pipewire-0').exists():break
                if server.poll() is not None:raise RuntimeError('Isolated PipeWire server failed')
                time.sleep(.05)
            process=subprocess.Popen([str(binary),'--socket',str(path)],env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
            for _ in range(100):
                if path.exists():
                    break
                if process.poll() is not None:
                    raise RuntimeError(process.communicate()[1])
                time.sleep(.05)
            assert stat.S_IMODE(path.stat().st_mode)==0o600
            duplicate=subprocess.run([str(binary),'--socket',str(Path(directory)/'other.sock')],env=env,capture_output=True,text=True,timeout=5)
            assert duplicate.returncode==1 and 'already running' in duplicate.stderr,duplicate.stderr
            with socket.socket(socket.AF_UNIX) as client:
                client.settimeout(5);client.connect(str(path));stream=client.makefile('rw')
                sequence=0
                def command(name,args=None,ok=True):
                    nonlocal sequence
                    sequence+=1
                    stream.write(json.dumps({'id':sequence,'command':name,'args':args or {}})+'\n');stream.flush()
                    response=json.loads(stream.readline())
                    assert response['id']==sequence and response['ok']==ok,response
                    return response.get('data',response.get('error'))
                status=command('status')
                assert status['status']=='unconfigured' and status['npu']['inferences']==0
                assert not status['captureConnected'] and not status['referenceConnected']
                assert isinstance(command('devices')['inputs'],list)
                command('not-a-command',ok=False)
                command('configure',{'inputId':'absent-input'},ok=False)
                command('configure',{'aecBackend':'AUTO'},ok=False)
                # A requested NPU with unavailable native libraries must remain
                # a visible failure, even while the DSP fallback is available.
                command('configure',{'aecEnabled':True,'aecBackend':'npu',
                                     'npuRuntimeLibrary':'/nonexistent/libopenvino_c.so',
                                     'npuModelDirectory':'/nonexistent/models'})
                for _ in range(100):
                    failed_npu=command('status')
                    if failed_npu['npu']['status']=='error':break
                    time.sleep(.01)
                assert failed_npu['npu']['status']=='error',failed_npu
                assert failed_npu['npu']['error'] and failed_npu['npu']['inferences']==0
                assert failed_npu['activeBackend']=='dsp' and failed_npu['degraded']
                assert failed_npu['npu']['device']=='',failed_npu
                command('configure',{'aecEnabled':False,'aecBackend':'bypass'})
                command('configure',{'eqEnabled':True},ok=False)
                command('configure',{'leftFilters':[{'frequency':24000,'gain':0,'q':1}]},ok=False)
                command('configure',{'unknown':1},ok=False)
                status=command('restore',{'inputId':'temporarily-unplugged','outputId':'temporarily-unplugged-output'})
                assert status['inputId']=='temporarily-unplugged' and not status['captureConnected']
                command('restore',{'inputId':'personal-tools-mic'},ok=False)
                command('restore',{'inputId':'','outputId':''})
                channel=[{'frequency':1000,'gain':-3,'q':1}]
                status=command('configure',{'leftFilters':channel,'rightFilters':channel,'eqEnabled':True})
                assert status['eqEnabled'] and status['leftFilters'][0]['gain']==-3
                command('import-eq',{'channel':'left','path':'/nonexistent-filter.txt'},ok=False)
                assert command('status')['leftFilters']==status['leftFilters']
                # Malformed JSON must not drop the connection or claim success.
                stream.write('{\n');stream.flush();assert json.loads(stream.readline())['ok'] is False
                stream.write(json.dumps({'id':99,'command':'status','args':{}})+'\n'+json.dumps({'id':100,'command':'devices','args':{}})+'\n');stream.flush()
                assert json.loads(stream.readline())['id']==99
                assert json.loads(stream.readline())['id']==100
                command('shutdown')
            assert process.wait(timeout=10)==0
            assert not path.exists()
            return {'ok':True,'isolatedServer':True,'checks':['private-socket','single-endpoint-owner','real-status','devices','invalid-command','invalid-device','restore-unplugged-device','reject-feedback-loop','explicit-backend','npu-failure-honesty','eq-validation','atomic-import-failure','malformed-json','multiple-lines','shutdown-cleanup']}
        finally:
            if process is not None and process.poll() is None:
                process.terminate()
                try:process.wait(timeout=10)
                except subprocess.TimeoutExpired:process.kill();process.wait()
            if server.poll() is None:
                server.terminate()
                try:server.wait(timeout=5)
                except subprocess.TimeoutExpired:server.kill();server.wait()

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--binary',type=Path,required=True);args=parser.parse_args()
    print(json.dumps(run(args.binary.resolve())))
