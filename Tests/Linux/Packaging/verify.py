#!/usr/bin/env python3
"""Read-only .deb/installed-file QA; never starts, stops or installs a service.

Unpacks only into a private temporary directory. Native dependencies are inspected
with readelf/ldd (the application entry points are never executed). Credential
findings contain a path and line number, never the matching value. This checks
known credential formats and private-data filenames, not every possible secret.
"""
import argparse
import configparser
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parents[3]
SERVICES = ['personal-tools.service', 'personal-tools-audio.service']
EXTENSION = 'usr/share/gnome-shell/extensions/personal-tools@griplabs.io'
REQUIRED = [
    'usr/bin/personal-tools', 'usr/bin/personal-tools-setup',
    'usr/lib/personal-tools/service/personal-tools-service',
    'usr/lib/personal-tools/service/personal-tools-service.runtimeconfig.json',
    'usr/lib/personal-tools/audio/PersonalToolsAudio',
    'usr/lib/personal-tools/run-audio.sh', 'usr/lib/personal-tools/prepare-npu.py',
    *[f'usr/lib/systemd/user/{name}' for name in SERVICES],
    'usr/share/dbus-1/services/io.griplabs.PersonalTools.service',
    'usr/share/applications/io.griplabs.PersonalTools.App.desktop',
    'usr/share/icons/hicolor/scalable/apps/io.griplabs.PersonalTools.App.svg',
    'usr/share/personal-tools/App/app.js', 'usr/share/personal-tools/Design/fonts.json',
    'usr/share/personal-tools/Vllm/runtime.py',
    'usr/share/personal-tools/Resources/EQ/L.txt', 'usr/share/personal-tools/Resources/EQ/R.txt',
    f'{EXTENSION}/extension.js', f'{EXTENSION}/metadata.json', f'{EXTENSION}/tokens.json',
    f'{EXTENSION}/schemas/gschemas.compiled',
    f'{EXTENSION}/native/libpt-clipboard.so', f'{EXTENSION}/native/PtClipboard-1.0.typelib',
]


def command(*args):
    return subprocess.run(args, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=90)


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def service_states():
    result = {}
    for name in SERVICES:
        process = command('systemctl', '--user', 'show', name, '--property=ActiveState', '--value')
        result[name] = process.stdout.strip() if process.returncode == 0 else 'unavailable'
    return result


def ini(path):
    value = configparser.ConfigParser(interpolation=None, strict=False)
    value.optionxform = str
    value.read(path)
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--deb', type=Path)
    parser.add_argument('--installed-root', type=Path, default=Path('/'))
    parser.add_argument('--report', type=Path, default=ROOT/'build/linux/packaging-qa.json')
    args = parser.parse_args()
    package = args.deb or max((ROOT/'build/linux/packages').glob('personal-tools_*.deb'), key=lambda p: p.stat().st_mtime)
    report = {'observedAt': datetime.now(timezone.utc).isoformat(), 'package': str(package.resolve()),
              'packageSha256': digest(package), 'readOnly': True, 'checks': [],
              'scope': 'Archive and installed files only; no install, upgrade, removal, login or service execution.'}

    def check(name, passed, details=None):
        report['checks'].append({'name': name, 'passed': bool(passed), 'details': details})

    before = service_states()
    report['servicesBefore'] = before
    check('services-inactive-before', all(value == 'inactive' for value in before.values()), before)
    metadata = command('dpkg-deb', '-f', str(package), 'Package', 'Version', 'Architecture', 'Depends')
    report['metadata'] = metadata.stdout.strip()
    check('package-metadata', metadata.returncode == 0 and 'Package: personal-tools' in metadata.stdout and 'Architecture: amd64' in metadata.stdout)

    with tempfile.TemporaryDirectory(prefix='personal-tools-package-qa-') as directory:
        root = Path(directory)/'root'
        controls = Path(directory)/'control'
        # Inspect every tar header before asking dpkg to extract this local build.
        headers = []
        stream = subprocess.Popen(['dpkg-deb', '--fsys-tarfile', str(package)], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        with tarfile.open(fileobj=stream.stdout, mode='r|') as archive:
            for member in archive:
                path = PurePosixPath(member.name.removeprefix('./'))
                safe = not path.is_absolute() and '..' not in path.parts and (str(path) == '.' or path.parts[0] == 'usr')
                headers.append({'path': str(path), 'safe': safe, 'mode': member.mode, 'uid': member.uid, 'gid': member.gid,
                                'typeSafe': member.isdir() or member.isfile(), 'directory': member.isdir()})
        stream.stdout.close()
        tar_result = stream.wait(timeout=90)
        unsafe = [item['path'] for item in headers if not item['safe'] or not item['typeSafe']]
        check('archive-paths-and-types', tar_result == 0 and not unsafe, unsafe)
        if unsafe or tar_result:
            raise RuntimeError('Refusing to extract invalid archive; see archive-paths-and-types')
        bad_permissions = [item['path'] for item in headers if item['uid'] != 0 or item['gid'] != 0 or item['mode'] not in ([0o755] if item['directory'] else [0o644, 0o755])]
        check('archive-root-ownership-and-permissions', not bad_permissions, bad_permissions)
        subprocess.run(['dpkg-deb', '-x', str(package), str(root)], check=True)
        subprocess.run(['dpkg-deb', '-e', str(package), str(controls)], check=True)
        maintainer_scripts = [p.name for p in controls.iterdir() if p.name in {'preinst', 'postinst', 'prerm', 'postrm', 'config', 'triggers'}]
        check('no-install-time-service-or-download-hooks', not maintainer_scripts, maintainer_scripts)
        missing = [name for name in REQUIRED if not (root/name).is_file()]
        check('required-package-files', not missing, missing)

        payload = [path for path in root.rglob('*') if path.is_file()]
        mismatch = []
        bad_installed_modes = []
        for source in payload:
            relative = source.relative_to(root)
            installed = args.installed_root/relative
            if not installed.is_file() or installed.is_symlink() or digest(source) != digest(installed):
                mismatch.append(str(relative))
                continue
            status = installed.stat()
            if status.st_uid != 0 or status.st_gid != 0 or stat.S_IMODE(status.st_mode) != stat.S_IMODE(source.stat().st_mode):
                bad_installed_modes.append(str(relative))
        check('installed-payload-byte-identical', not mismatch, {'filesChecked': len(payload), 'mismatches': mismatch})
        check('installed-root-ownership-and-permissions', not bad_installed_modes, bad_installed_modes)
        installed_version = command('dpkg-query', '-W', '-f=${Version} ${Status}', 'personal-tools')
        report['installedPackage'] = installed_version.stdout.strip()
        check('dpkg-installed-status', installed_version.returncode == 0 and installed_version.stdout.endswith('install ok installed'))

        fonts = json.loads((root/'usr/share/personal-tools/Design/fonts.json').read_text())
        font_checks = []
        for font in fonts['fonts']:
            relative = Path('usr/share/personal-tools/Design')/font['bundledFile']
            bundled = root/relative
            installed = args.installed_root/relative
            font_checks.append({'file': str(relative), 'sha256': font['sha256'], 'version': font['fontVersion'],
                                'packageMatches': bundled.is_file() and digest(bundled) == font['sha256'],
                                'installedMatches': installed.is_file() and digest(installed) == font['sha256']})
        check('bundled-fonts-sha256', len(font_checks) == 4 and all(item['packageMatches'] and item['installedMatches'] for item in font_checks), font_checks)
        licenses = root/'usr/share/personal-tools/Design/fonts'
        check('bundled-font-licenses', all((licenses/name).is_file() for name in ['LICENSE-fonts-ubuntu', 'LICENSE-fonts-noto-cjk']))

        desktop = root/'usr/share/applications/io.griplabs.PersonalTools.App.desktop'
        entry = ini(desktop)['Desktop Entry']
        validation = command('desktop-file-validate', str(desktop))
        check('desktop-file-valid', validation.returncode == 0, validation.stderr.strip())
        check('desktop-id-executable-icon', entry.get('Exec') == 'personal-tools' and entry.get('Icon') == desktop.stem
              and (root/f'usr/share/icons/hicolor/scalable/apps/{desktop.stem}.svg').is_file()
              and bool((root/'usr/bin/personal-tools').stat().st_mode & stat.S_IXUSR))
        dbus = ini(root/'usr/share/dbus-1/services/io.griplabs.PersonalTools.service')['D-BUS Service']
        main_unit = ini(root/'usr/lib/systemd/user/personal-tools.service')
        audio_unit = ini(root/'usr/lib/systemd/user/personal-tools-audio.service')
        check('dbus-systemd-executable-consistency', dbus.get('Name') == 'io.griplabs.PersonalTools'
              and dbus.get('SystemdService') == 'personal-tools.service'
              and dbus.get('Exec') == main_unit['Service'].get('ExecStart')
              and main_unit['Service'].get('BusName') == dbus.get('Name')
              and all((root/unit['Service']['ExecStart'].lstrip('/')).is_file() for unit in [main_unit, audio_unit])
              and all(unit['Service'].get('UMask') == '0077' for unit in [main_unit, audio_unit]))
        check('graphical-session-autostart-lifecycle', main_unit['Install'].get('WantedBy') == 'graphical-session.target'
              and main_unit['Unit'].get('PartOf') == 'graphical-session.target'
              and audio_unit['Unit'].get('PartOf') == 'personal-tools.service')
        validation = command('systemd-analyze', '--user', 'verify', *[str(root/f'usr/lib/systemd/user/{name}') for name in SERVICES])
        check('systemd-unit-syntax', validation.returncode == 0, validation.stderr.strip())
        extension = json.loads((root/EXTENSION/'metadata.json').read_text())
        check('gnome-extension-version-and-schema', extension['uuid'] == 'personal-tools@griplabs.io' and extension['shell-version'] == ['50']
              and (root/EXTENSION/'schemas'/f"{extension['settings-schema']}.gschema.xml").is_file())
        shell_tokens = root/EXTENSION/'tokens.json'
        check('shared-design-token-copy', shell_tokens.is_file() and digest(shell_tokens) == digest(root/'usr/share/personal-tools/Design/tokens.json'))

        native = []
        for path in payload:
            with path.open('rb') as file:
                if file.read(4) != b'\x7fELF':
                    continue
            dynamic = command('readelf', '-d', str(path))
            resolved = command('ldd', str(path))
            native.append({'file': str(path.relative_to(root)),
                           'needed': re.findall(r'Shared library: \[([^]]+)\]', dynamic.stdout),
                           'runpaths': re.findall(r'Library (?:runpath|rpath): \[([^]]+)\]', dynamic.stdout),
                           'unresolved': [line.strip() for line in resolved.stdout.splitlines() if 'not found' in line],
                           'readelfSuccess': dynamic.returncode == 0, 'lddSuccess': resolved.returncode == 0})
        check('native-linked-libraries', bool(native) and all(not item['unresolved'] and item['readelfSuccess'] and item['lddSuccess'] for item in native), native)
        check('no-build-directory-runtime-library-path', all(not any('/home/' in value or '/build/' in value or '/tmp/' in value for value in item['runpaths']) for item in native))

        private_names = re.compile(r'(^|/)(?:\.env(?:\..*)?|credentials\.json|secrets\.json|tokens?\.json|settings\.json|auth\.json|id_(?:rsa|ed25519)|.*\.(?:pem|key|p12|pfx|sqlite|sqlite3|db|wav|flac))$', re.I)
        public_design_manifests = {'usr/share/personal-tools/Design/tokens.json', f'{EXTENSION}/tokens.json'}
        forbidden = [str(path.relative_to(root)) for path in payload if str(path.relative_to(root)) not in public_design_manifests and (private_names.search(str(path.relative_to(root))) or any(part in {'.ssh', '.aws', '.git', '.venv', 'node_modules', 'logs', 'recordings', 'archive'} for part in path.relative_to(root).parts))]
        check('no-known-private-data-or-secret-filenames', not forbidden, forbidden)
        patterns = {
            'private-key': re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
            'provider-token': re.compile(r'\b(?:sk-[A-Za-z0-9_-]{24,}|hf_[A-Za-z0-9]{25,}|gh[pousr]_[A-Za-z0-9]{30,}|AKIA[A-Z0-9]{16})\b'),
            'credential-literal': re.compile(r'''(?i)["']?(?:api[_-]?key|client[_-]?secret|password)["']?\s*[:=]\s*["'][A-Za-z0-9_-]{20,}["']'''),
        }
        findings = []
        text_files = 0
        for path in payload:
            if path.stat().st_size > 2 * 1024 * 1024:
                continue
            data = path.read_bytes()
            if b'\0' in data:
                continue
            try:
                content = data.decode('utf-8')
            except UnicodeDecodeError:
                continue
            text_files += 1
            for kind, pattern in patterns.items():
                for match in pattern.finditer(content):
                    findings.append({'file': str(path.relative_to(root)), 'line': content.count('\n', 0, match.start())+1, 'kind': kind})
        check('no-known-secret-content', not findings, {'textFilesChecked': text_files, 'findings': findings, 'limitation': 'Known patterns only; arbitrary credentials cannot be proven absent.'})

        drift = []
        for source_name, target_name in [('Sources/Linux/App', 'usr/share/personal-tools/App'), ('Sources/Linux/Shell', EXTENSION), ('Sources/Linux/Design', 'usr/share/personal-tools/Design'), ('Sources/Linux/Vllm', 'usr/share/personal-tools/Vllm'), ('Resources/EQ', 'usr/share/personal-tools/Resources/EQ')]:
            for source in (ROOT/source_name).rglob('*'):
                if source.is_file() and '__pycache__' not in source.parts:
                    target = root/target_name/source.relative_to(ROOT/source_name)
                    if target.is_file() and digest(source) != digest(target):
                        drift.append(str(source.relative_to(ROOT)))
        for name, target_name in [('personal-tools.service', 'usr/lib/systemd/user/personal-tools.service'),
                                  ('personal-tools-setup', 'usr/bin/personal-tools-setup')]:
            source = ROOT/'Sources/Linux/Packaging'/name
            target = root/target_name
            if not target.is_file() or digest(source) != digest(target):
                drift.append(str(source.relative_to(ROOT)))
        report['sourceDriftSincePackageBuild'] = drift
        report['sourceDriftNote'] = 'Rebuild and reinstall before final delivery if this list is nonempty; existing package integrity is checked separately.'

    after = service_states()
    report['servicesAfter'] = after
    check('services-unchanged-and-inactive-after', after == before and all(value == 'inactive' for value in after.values()), after)
    report['passed'] = all(item['passed'] for item in report['checks'])
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n')
    for result in report['checks']:
        print(f"{'PASS' if result['passed'] else 'FAIL'} {result['name']}")
    print(f"Source drift: {len(report['sourceDriftSincePackageBuild'])} files; report: {args.report}")
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
