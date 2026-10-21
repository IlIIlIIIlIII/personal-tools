#!/usr/bin/env python3
"""Check that processed endpoints follow their physical devices under WirePlumber.

Runs the real engine against a private PipeWire/WirePlumber policy session with
synthetic devices only. While the configured desktop default is a processed
endpoint, unplugging its physical device must fall back to another device,
replugging it must return to the processed endpoint, and disconnected headphones
must fall back to the processed endpoint rather than its bare device.
"""
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time


def wait_for(predicate, message):
    last = None
    for _ in range(200):
        try:
            last = predicate()
            if last:
                return last
        except (OSError, KeyError, StopIteration):
            pass
        time.sleep(.05)
    raise AssertionError(f'{message}: {last}')


def run(binary):
    processes = []
    with tempfile.TemporaryDirectory(prefix='pt-endpoint-fallback-') as directory:
        root = Path(directory)
        env = os.environ.copy()
        env.update(XDG_RUNTIME_DIR=directory, PIPEWIRE_RUNTIME_DIR=directory, PIPEWIRE_REMOTE='pipewire-0',
                   XDG_CONFIG_HOME=str(root / 'config'), XDG_DATA_HOME=str(root / 'data'),
                   XDG_STATE_HOME=str(root / 'state'), XDG_CACHE_HOME=str(root / 'cache'))
        configuration = root / 'pipewire.conf'
        configuration.write_text(Path('/usr/share/pipewire/pipewire.conf').read_text().replace(
            '#audiotestsrc   = audiotestsrc/libspa-audiotestsrc',
            'audiotestsrc   = audiotestsrc/libspa-audiotestsrc'))
        log = (root / 'session.log').open('w')

        def start(arguments):
            process = subprocess.Popen(arguments, env=env, stdout=log, stderr=log)
            processes.append(process)
            return process

        def output(*arguments):
            return subprocess.check_output(arguments, env=env, text=True, timeout=10)

        def graph():
            return json.loads(output('pw-dump'))

        def node(name):
            return next(item for item in graph() if item.get('type') == 'PipeWire:Interface:Node'
                        and item.get('info', {}).get('props', {}).get('node.name') == name)

        def exists(name):
            try:
                node(name)
                return True
            except StopIteration:
                return False

        def default(kind):
            for item in graph():
                if item.get('props', {}).get('metadata.name') != 'default':
                    continue
                for row in item.get('metadata', []):
                    if row.get('subject') == 0 and row.get('key') == f'default.audio.{kind}':
                        return row['value']['name']
            return None

        def device(name, media, priority):
            factory = 'support.null-audio-sink' if media == 'Audio/Sink' else 'audiotestsrc'
            output('pw-cli', 'create-node', 'adapter', '{ factory.name=' + factory + ' node.name=' + name +
                   ' media.class=' + media + ' audio.channels=2 audio.position=[ FL FR ] object.linger=true '
                   'priority.session=' + str(priority) + ' adapter.auto-port-config={ mode=dsp position=preserve } }')
            wait_for(lambda: exists(name), f'{name} was not created')

        def unplug(name):
            output('pw-cli', 'destroy', str(node(name)['id']))
            wait_for(lambda: not exists(name), f'{name} was not removed')

        def command(name, args=None):
            with socket.socket(socket.AF_UNIX) as client:
                client.settimeout(5)
                client.connect(str(root / 'engine/audio.sock'))
                client.sendall((json.dumps({'id': 1, 'command': name, 'args': args or {}}) + '\n').encode())
                response = json.loads(client.makefile('r').readline())
                assert response['ok'], response
                return response['data']

        def select(kind, name):
            output('pw-metadata', '-n', 'default', '0', f'default.configured.audio.{kind}',
                   json.dumps({'name': name}), 'Spa:String:JSON')

        try:
            start(['pipewire', '-c', str(configuration)])
            wait_for(lambda: (root / 'pipewire-0').exists(), 'Private PipeWire did not start')
            start(['wireplumber', '--profile', 'policy'])
            # Physical USB devices outrank the built-in ones, as on real hardware.
            device('laptop-speakers', 'Audio/Sink', 1000)
            device('usb-dac', 'Audio/Sink', 1100)
            device('laptop-mic', 'Audio/Source', 2000)
            device('usb-mic', 'Audio/Source', 2100)
            wait_for(lambda: default('sink') == 'usb-dac' and default('source') == 'usb-mic',
                     'WirePlumber did not select the synthetic defaults')
            path = root / 'engine/audio.sock'
            start([str(binary.resolve()), '--socket', str(path)])
            wait_for(path.exists, 'Audio engine did not create socket')
            assert not exists('personal-tools-output') and not exists('personal-tools-mic'), \
                'an unconfigured engine must not publish silent endpoints'
            command('configure', {'outputId': 'usb-dac', 'inputId': 'usb-mic'})
            wait_for(lambda: exists('personal-tools-output') and exists('personal-tools-mic'),
                     'Processed endpoints were not published for present devices')
            select('sink', 'personal-tools-output')
            select('source', 'personal-tools-mic')
            wait_for(lambda: default('sink') == 'personal-tools-output' and default('source') == 'personal-tools-mic',
                     'WirePlumber did not apply the processed defaults')

            unplug('usb-dac')
            wait_for(lambda: default('sink') == 'laptop-speakers',
                     'Unplugged output kept the silent processed sink as the default')
            assert not exists('personal-tools-output')
            assert default('source') == 'personal-tools-mic', 'output unplug must not move the microphone'
            device('usb-dac', 'Audio/Sink', 1100)
            wait_for(lambda: default('sink') == 'personal-tools-output',
                     'Replugged output did not return to the processed sink')

            # Bluetooth headphones outrank wired outputs. Once they leave, the
            # remembered fallback must be the processed sink, not its bare DAC.
            device('earbuds', 'Audio/Sink', 2010)
            select('sink', 'earbuds')
            wait_for(lambda: default('sink') == 'earbuds', 'WirePlumber did not apply the headphones')
            unplug('earbuds')
            wait_for(lambda: default('sink') == 'personal-tools-output',
                     'Disconnected headphones fell back to the unprocessed output device')

            unplug('usb-mic')
            wait_for(lambda: default('source') == 'laptop-mic',
                     'Unplugged microphone kept the silent processed source as the default')
            assert not exists('personal-tools-mic')
            device('usb-mic', 'Audio/Source', 2100)
            wait_for(lambda: default('source') == 'personal-tools-mic',
                     'Replugged microphone did not return to the processed source')

            command('configure', {'outputId': '', 'inputId': ''})
            wait_for(lambda: not exists('personal-tools-output') and not exists('personal-tools-mic'),
                     'Clearing devices did not withdraw the processed endpoints')
            command('shutdown')
            return {'ok': True, 'isolatedServer': True, 'checks': ['unconfigured-hidden', 'present-published',
                    'output-unplug-fallback', 'output-replug-return', 'headphones-leave-processed',
                    'microphone-unplug-fallback',
                    'microphone-replug-return', 'cleared-withdrawn']}
        except AssertionError:
            log.flush()
            print((root / 'session.log').read_text()[-6000:], file=sys.stderr)
            raise
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
    parser.add_argument('--inside', action='store_true')
    args = parser.parse_args()
    if args.inside:
        print(json.dumps(run(args.binary)))
        raise SystemExit(0)
    # WirePlumber needs a session bus; keep it private to this test.
    raise SystemExit(subprocess.run(['dbus-run-session', '--', sys.executable, __file__, *sys.argv[1:], '--inside']).returncode)
