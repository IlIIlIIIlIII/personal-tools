#!/usr/bin/env python3
"""Real PipeWire lifecycle checks on an isolated server with synthetic devices.

Never connects to or restarts the desktop user's PipeWire server. Exercises
device disappearance/reappearance, persisted missing-device restore, monitor
exclusion, mute readback, hotplug mute, clean shutdown, and server loss.
"""
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time


def wait_for(predicate, message):
    last = None
    for _ in range(150):
        try:
            last = predicate()
            if last:
                return last
        except (OSError, KeyError, StopIteration):
            pass
        time.sleep(.04)
    raise AssertionError(f'{message}: {last}')


def run(binary):
    processes = []
    with tempfile.TemporaryDirectory(prefix='pt-pw-lifecycle-') as directory:
        root = Path(directory)
        env = os.environ.copy()
        env.update(XDG_RUNTIME_DIR=directory, PIPEWIRE_RUNTIME_DIR=directory, PIPEWIRE_REMOTE='pipewire-0')
        env.pop('DBUS_SESSION_BUS_ADDRESS', None)
        # System default has no ALSA enumerator: hardware enumeration belongs to
        # WirePlumber, which this test never starts. Enable only synthetic SPA.
        configuration = root / 'pipewire.conf'
        configuration.write_text(Path('/usr/share/pipewire/pipewire.conf').read_text().replace(
            '#audiotestsrc   = audiotestsrc/libspa-audiotestsrc',
            'audiotestsrc   = audiotestsrc/libspa-audiotestsrc'))
        log = (root / 'server.log').open('w')

        def start_server():
            server = subprocess.Popen(['pipewire', '-c', str(configuration)], env=env, stdout=log, stderr=log)
            processes.append(server)
            wait_for(lambda: (root / 'pipewire-0').exists(), 'Private PipeWire did not start')
            return server

        def graph():
            return json.loads(subprocess.check_output(['pw-dump'], env=env, text=True, timeout=5))

        def node(name):
            return next(item for item in graph() if item.get('info', {}).get('props', {}).get('node.name') == name)

        def cli(*arguments):
            subprocess.run(['pw-cli', *arguments], env=env, check=True, capture_output=True, text=True, timeout=5)

        def source(name, monitor=False):
            cli('create-node', 'adapter', '{ factory.name=audiotestsrc node.name=' + name +
                ' media.class=Audio/Source audio.channels=1 audio.position=[ MONO ] object.linger=true '
                'adapter.auto-port-config={ mode=dsp position=preserve } '
                + ('device.class=monitor ' if monitor else '') + '}')
            identifier=wait_for(lambda: node(name), 'Source creation failed')['id']
            # No session manager runs here, so explicitly negotiate the source's
            # DSP output ports (the null sink configures its inputs itself).
            cli('set-param', str(identifier), 'PortConfig',
                '{ direction=Output mode=dsp format={ mediaType=audio mediaSubtype=raw '
                'format=F32P rate=48000 channels=1 position=[ MONO ] } }')
            return identifier

        def sink():
            cli('create-node', 'adapter', '{ factory.name=support.null-audio-sink node.name=test-output '
                'media.class=Audio/Sink audio.channels=2 audio.position=[ FL FR ] object.linger=true '
                'adapter.auto-port-config={ mode=dsp monitor=true position=preserve } }')
            return wait_for(lambda: node('test-output'), 'Sink creation failed')['id']

        def start_engine():
            path = root / 'engine/audio.sock'
            engine = subprocess.Popen([str(binary.resolve()), '--socket', str(path)], env=env, stdout=log, stderr=log)
            processes.append(engine)
            wait_for(path.exists, 'Audio engine did not create socket')
            return engine

        def command(name, args=None, ok=True):
            with socket.socket(socket.AF_UNIX) as client:
                client.settimeout(5)
                client.connect(str(root / 'engine/audio.sock'))
                client.sendall((json.dumps({'id': 1, 'command': name, 'args': args or {}}) + '\n').encode())
                response = json.loads(client.makefile('r').readline())
                assert response['ok']==ok, response
                return response.get('data',response.get('error'))

        def mute_state(name):
            return next(device['muted'] for device in command('devices')['inputs'] if device['id'] == name)

        try:
            server = start_server()
            source('test-input')
            source('test-monitor', True)
            sink()
            engine = start_engine()
            try:
                wait_for(lambda: len(command('devices')['inputs']) == 1, 'Monitor source was not excluded')
            except AssertionError as error:
                raise AssertionError(f'{error}; devices={command("devices")}') from error
            command('configure', {'inputId':'test-monitor'}, ok=False)
            command('restore', {'inputId':'test-monitor'}, ok=False)
            command('configure', {'outputId':'test-input'}, ok=False)
            command('configure', {'inputId':'personal-tools-mic'}, ok=False)
            command('configure', {'inputId': 'test-input', 'outputId': 'test-output'})
            wait_for(lambda: command('status')['captureConnected'], 'Capture did not link')
            cli('destroy', str(node('test-input')['id']))
            wait_for(lambda: not command('status')['captureConnected'], 'Removed source still appears connected')
            assert command('status')['inputId'] == 'test-input'
            source('test-input')
            wait_for(lambda: command('status')['captureConnected'], 'Recreated source did not relink')
            assert command('mute', {'muted': True})['muteResult']['verified']
            source('test-hotplug')
            wait_for(lambda: mute_state('test-hotplug') is True, 'Hotplug microphone did not inherit global mute')
            command('shutdown')
            assert engine.wait(timeout=5) == 0
            assert not (root / 'engine/audio.sock').exists()
            # The synthetic original source must have been restored to unmuted.
            props = node('test-input')['info']['params']['Props']
            assert any(prop.get('mute') is False for prop in props), props
            engine = start_engine()
            command('restore', {'inputId': 'not-yet-attached', 'outputId': 'test-output'})
            assert not command('status')['captureConnected']
            source('not-yet-attached')
            wait_for(lambda: command('status')['captureConnected'], 'Saved absent source did not link on hotplug')
            server.terminate()
            server.wait(timeout=5)
            exit_code=engine.wait(timeout=5)
            log.flush()
            assert exit_code == 1, (f'Disconnected engine must exit cleanly for supervisor restart; got {exit_code}; '
                                    + (root/'server.log').read_text()[-4000:])
            assert not (root / 'engine/audio.sock').exists()
            server = start_server()
            engine = start_engine()
            command('restore', {'inputId': 'test-input', 'outputId': 'test-output'})
            source('test-input')
            sink()
            wait_for(lambda: command('status')['captureConnected'], 'Server restart/replay did not recover')
            command('shutdown')
            assert engine.wait(timeout=5) == 0
            return {'ok': True, 'isolatedServer': True, 'checks': ['monitor-exclusion', 'invalid-target-rejection', 'device-removal',
                'device-reconnect', 'mute-readback', 'hotplug-mute', 'shutdown-restore',
                'missing-device-replay', 'pipewire-disconnect-exit', 'server-restart-replay']}
        finally:
            for process in reversed(processes):
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait()
            log.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', type=Path, required=True)
    print(json.dumps(run(parser.parse_args().binary)))
