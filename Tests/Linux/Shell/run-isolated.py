#!/usr/bin/python3
"""Build and exercise the extension on a private bus, XDG tree and compositor."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile

root = Path(__file__).resolve().parents[3]
parser = argparse.ArgumentParser()
parser.add_argument('--script', default='smoke.js')
parser.add_argument('--size', default='1488x1056')
parser.add_argument('--output', default=str(root / 'build/linux/shell-qa'))
parser.add_argument('--no-extension', action='store_true')
parser.add_argument('--real-service', action='store_true')
parser.add_argument('--theme', choices=['dark','light'], default='dark')
parser.add_argument('--text-scale', type=float, default=1.0)
parser.add_argument('--scale', type=float, default=1.0)
parser.add_argument('--second-monitor')
parser.add_argument('--monitor-scales')
args = parser.parse_args()
build = root / 'build/linux/shell'
if not (build / 'build.ninja').exists():
    subprocess.run(['meson', 'setup', str(build), str(root / 'Sources/Linux/Shell')], check=True)
subprocess.run(['meson', 'compile', '-C', str(build)], check=True)
subprocess.run(['python3', str(root / 'Scripts/Linux/generate-design.py')], check=True)
output = Path(args.output).resolve()
output.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix='personal-tools-shell-test-', ignore_cleanup_errors=True) as temporary:
    runtime = Path(temporary) / 'runtime'
    runtime.mkdir(mode=0o700)
    staging = Path(temporary) / 'extension'
    shutil.copytree(root / 'Sources/Linux/Shell', staging)
    shutil.copy(root / 'Sources/Linux/Design/tokens.json', staging)
    for name in ['libpt-clipboard.so', 'PtClipboard-1.0.typelib']:
        shutil.copy(build / 'native' / name, staging / 'native')
    subprocess.run(['glib-compile-schemas', str(staging / 'schemas')], check=True)
    archive = Path(temporary) / 'personal-tools@griplabs.io.shell-extension.zip'
    with zipfile.ZipFile(archive, 'w') as packed:
        for path in staging.rglob('*'):
            if path.is_file():
                packed.write(path, path.relative_to(staging))
    env = dict(os.environ, PERSONALTOOLS_TEST_SIZE=args.size, PERSONALTOOLS_TEST_OUTPUT=str(output),
               PERSONALTOOLS_TEST_ROOT=str(root), PERSONALTOOLS_TEST_REAL_SERVICE=str(int(args.real_service)),
               PERSONALTOOLS_TEST_THEME=args.theme, PERSONALTOOLS_TEST_TEXT_SCALE=str(args.text_scale), PERSONALTOOLS_TEST_SCALE=str(args.scale),
               PERSONALTOOLS_TEST_SECOND_MONITOR=args.second_monitor or '',PERSONALTOOLS_TEST_MONITOR_SCALES=args.monitor_scales or '',
               PERSONALTOOLS_TEST_PRIVATE_ROOT=str(Path(temporary)),
               XDG_RUNTIME_DIR=str(runtime), LIBGL_ALWAYS_SOFTWARE='1', GSK_RENDERER='cairo')
    # Never inherit the real desktop's session bus or mutate its extension list.
    env.pop('DBUS_SESSION_BUS_ADDRESS', None)
    command = ['dbus-run-session', '--', 'gnome-shell-test-tool', '--headless', '--disable-animations',
               '--wrap', f'python3 {Path(__file__).with_name("compositor-wrapper.py")}',
               '--extra-filter', 'io.griplabs.PersonalTools.App', '--extra-filter', 'io.griplabs.PersonalTools.Preview',
               '--extra-filter', 'io.griplabs.PersonalTools', '--extra-filter', 'io.griplabs.PersonalTools.Shell',
               '--extra-filter', 'io.griplabs.PersonalTools.Preview.Source', '--extra-filter', 'io.griplabs.PersonalTools.Preview.Target',
               '--extra-filter', 'org.gnome.Ptyxis',
               # GJS GTK clients use the gjs WM class on X11, unlike their
               # application ID on Wayland. Allow them only in this test Shell.
               '--extra-filter', 'gjs',
               *([] if args.no_extension else ['--extension', str(archive)]),
               str(Path(args.script).resolve() if Path(args.script).is_absolute() else Path(__file__).with_name(args.script))]
    with (output / 'shell.log').open('w') as log:
        result = subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=180)
    # The document portal's FUSE mount can briefly outlive the private bus.
    # Detach only this test's mount before TemporaryDirectory visits it.
    fuse = shutil.which('fusermount3')
    if fuse and os.path.ismount(runtime / 'doc'):
        subprocess.run([fuse, '-uz', str(runtime / 'doc')], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    transcript=(output / 'shell.log').read_text()
    print(transcript if result.returncode else '\n'.join(line for line in transcript.splitlines() if line.startswith('PT_')))
    print(f'Full compositor log: {output / "shell.log"}')
    raise SystemExit(result.returncode)
