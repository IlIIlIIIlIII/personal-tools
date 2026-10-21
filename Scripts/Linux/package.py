#!/usr/bin/env python3
"""Package already-built components. Never modifies the user's installation."""
import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT=Path(__file__).resolve().parents[2]
parser=argparse.ArgumentParser()
parser.add_argument('--version',default='0.7.0~ubuntu9')
args=parser.parse_args()
output=ROOT/'build/linux/packages';output.mkdir(parents=True,exist_ok=True)
with tempfile.TemporaryDirectory(prefix='personal-tools-deb-') as directory:
    stage=Path(directory)
    stage.chmod(0o755)
    def file(source,destination,executable=False):
        target=stage/destination;target.parent.mkdir(parents=True,exist_ok=True)
        shutil.copy2(ROOT/source,target)
        if executable:target.chmod(0o755)
    def tree(source,destination):
        shutil.copytree(ROOT/source,stage/destination,ignore=shutil.ignore_patterns('bin','obj','__pycache__'))
    tree('Sources/Linux/App','usr/share/personal-tools/App')
    tree('Sources/Linux/Vllm','usr/share/personal-tools/Vllm')
    tree('Sources/Linux/Design','usr/share/personal-tools/Design')
    tree('Resources/EQ','usr/share/personal-tools/Resources/EQ')
    tree('build/linux/publish/service','usr/lib/personal-tools/service')
    # EventPipe remains available; the optional .NET LTTng provider targets
    # an older liblttng-ust SONAME not provided by Ubuntu 26.04.
    (stage/'usr/lib/personal-tools/service/libcoreclrtraceptprovider.so').unlink(missing_ok=True)
    file('build/linux/native/Sources/Linux/Audio/PersonalToolsAudio','usr/lib/personal-tools/audio/PersonalToolsAudio',True)
    file('Scripts/linux-audio-run.sh','usr/lib/personal-tools/run-audio.sh',True)
    file('Scripts/linux-audio-prepare-npu.py','usr/lib/personal-tools/prepare-npu.py',True)
    for name in ['personal-tools.service','personal-tools-audio.service']:
        file(f'Sources/Linux/Packaging/{name}',f'usr/lib/systemd/user/{name}')
    file('Sources/Linux/Packaging/io.griplabs.PersonalTools.service','usr/share/dbus-1/services/io.griplabs.PersonalTools.service')
    file('Sources/Linux/Packaging/personal-tools','usr/bin/personal-tools',True)
    file('Sources/Linux/Packaging/personal-tools-setup','usr/bin/personal-tools-setup',True)
    file('Sources/Linux/Packaging/copyright-yaru','usr/share/doc/personal-tools/copyright-yaru')
    file('docs/linux/README.md','usr/share/doc/personal-tools/README.md')
    file('Sources/Linux/Packaging/io.griplabs.PersonalTools.App.desktop','usr/share/applications/io.griplabs.PersonalTools.App.desktop')
    file('Sources/Linux/Packaging/io.griplabs.PersonalTools.App.svg','usr/share/icons/hicolor/scalable/apps/io.griplabs.PersonalTools.App.svg')
    file('Sources/Linux/Packaging/io.griplabs.PersonalTools.App-symbolic.svg','usr/share/icons/hicolor/symbolic/apps/io.griplabs.PersonalTools.App-symbolic.svg')
    ext='usr/share/gnome-shell/extensions/personal-tools@griplabs.io'
    tree('Sources/Linux/Shell',ext)
    file('Sources/Linux/Design/tokens.json',f'{ext}/tokens.json')
    for name in ['libpt-clipboard.so','PtClipboard-1.0.typelib']:
        file(f'build/linux/shell/native/{name}',f'{ext}/native/{name}')
    subprocess.run(['glib-compile-schemas',str(stage/ext/'schemas')],check=True)
    control=stage/'DEBIAN';control.mkdir()
    installed_kib=sum(p.stat().st_size for p in stage.rglob('*') if p.is_file())//1024
    (control/'control').write_text(f'''Package: personal-tools
Version: {args.version}
Architecture: amd64
Installed-Size: {installed_kib}
Maintainer: Personal Tools contributors
Section: utils
Priority: optional
Depends: gjs (>= 1.88), gir1.2-gtk-4.0, gir1.2-adw-1, gnome-shell (>= 50), gnome-shell (<< 51), libpipewire-0.3-0t64, pipewire-bin, wireplumber, python3, fonts-ubuntu, fonts-noto-cjk, yaru-theme-icon, libicu78, libssl3t64
Recommends: build-essential, cmake
Description: Native personal tools for Ubuntu GNOME
 Clipboard archive, audio processing, display control and local AI services.
 NPU runtime and language model downloads are separate user actions.
''')
    # No maintainer script touches per-user history or silently starts services.
    package=output/f'personal-tools_{args.version}_amd64.deb'
    for path in stage.rglob('*'):
        if path.is_dir(): path.chmod(0o755)
        elif not path.is_symlink(): path.chmod(0o755 if path.stat().st_mode & 0o111 else 0o644)
    subprocess.run(['dpkg-deb','--root-owner-group','--build',str(stage),str(package)],check=True)
    print(package)
