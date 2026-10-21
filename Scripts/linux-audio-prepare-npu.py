#!/usr/bin/env python3
"""Install the tested NPU userspace payload into a user-owned directory.

No apt, sudo, firmware writes, kernel module changes, or global environment edits.
The Ubuntu24 OpenVINO 2026.3.1 build includes the NPU plugin absent from the
same-version Ubuntu26 archive. It has been tested on Ubuntu26.04/Arrow Lake.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import urllib.request

OPENVINO = 'https://storage.openvinotoolkit.org/repositories/openvino/packages/2026.3.1/linux/openvino_toolkit_ubuntu24_2026.3.1.22476.56d9685302d_x86_64.tgz'
DRIVER = 'https://github.com/intel/linux-npu-driver/releases/download/v1.38.0/linux-npu-driver-v1.38.0.20260910-34487311128-ubuntu2604.tar.gz'
LOADER = 'https://snapshot.ppa.launchpadcontent.net/kobuk-team/intel-graphics/ubuntu/20260830T100000Z/pool/main/l/level-zero-loader/libze1_1.32.0-1~26.04~ppa1_amd64.deb'
DOWNLOADS = [
    ('openvino-2026.3.1-ubuntu24.tgz', OPENVINO, 'cb84d1ccdecb8bd90337edf63e8d081a17cde2fc72f5e9feb213b5b2f96eb21b'),
    ('npu-driver-1.38.0-ubuntu26.tar.gz', DRIVER, '83685e6d92f4db1ca65e442846d487c7feb1335634b33994292ceb38f933bcc7'),
    ('libze1_1.32.0-ubuntu26.deb', LOADER, 'ddd80bf4c5cef21716bc1d7370cc055dc8803451c6e644a98d3dd534d91bc1b2'),
]
MODELS = {
    'dtln_aec_256_1.tflite': '4a3a588b69fd79d837bc068b579a26faa92cac39dddbb00001d2dc1c3d869d60',
    'dtln_aec_256_2.tflite': 'fa2590243aad1bf893c5be45b20709e8c50feec65e3604d1d52bae6eeddc23d3',
}

def digest(path):
    with path.open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()

def download(path, url, expected):
    if not path.exists() or digest(path) != expected:
        temporary = path.with_suffix(path.suffix + '.partial')
        with urllib.request.urlopen(url, timeout=90) as response, temporary.open('wb') as target:
            shutil.copyfileobj(response, target)
        if digest(temporary) != expected:
            temporary.unlink()
            raise RuntimeError(f'SHA256 mismatch: {url}')
        temporary.replace(path)
    return path

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, default=Path(os.environ.get('XDG_DATA_HOME', Path.home()/'.local/share'))/'personal-tools/npu')
    parser.add_argument('--cache', type=Path)
    parser.add_argument('--probe', type=Path, help='Path to PersonalToolsAudio; actual NPU execution is required for success')
    args = parser.parse_args()
    root = args.directory.expanduser().resolve()
    root.mkdir(parents=True, exist_ok=True)
    cache = (args.cache or root/'downloads').resolve()
    cache.mkdir(parents=True, exist_ok=True)
    for name, url, sha in DOWNLOADS:
        download(cache/name, url, sha)
    runtime = root/'openvino'
    runtime.mkdir(exist_ok=True)
    with tarfile.open(cache/DOWNLOADS[0][0]) as archive:
        # Archive members share one versioned root. data filter rejects traversal
        # and unsafe absolute symlinks while retaining relative .so links.
        staging=root/'runtime-extract'
        staging.mkdir(exist_ok=True)
        archive.extractall(staging, filter='data')
        extracted=next(p for p in staging.iterdir() if p.is_dir())
        shutil.copytree(extracted, runtime, dirs_exist_ok=True)
    packages=root/'driver-packages'
    packages.mkdir(exist_ok=True)
    with tarfile.open(cache/DOWNLOADS[1][0]) as archive:
        archive.extractall(packages, filter='data')
    userlib=root/'userspace'
    userlib.mkdir(exist_ok=True)
    for pattern in ('intel-driver-compiler-npu_*.deb', 'intel-level-zero-npu_*.deb'):
        matches=list(packages.glob(pattern))
        if len(matches)!=1:
            raise RuntimeError(f'Unexpected NPU archive layout: {pattern}')
        subprocess.run(['dpkg-deb','-x',str(matches[0]),str(userlib)],check=True)
    subprocess.run(['dpkg-deb','-x',str(cache/DOWNLOADS[2][0]),str(userlib)],check=True)
    models=root/'models'
    models.mkdir(exist_ok=True)
    for name, sha in MODELS.items():
        download(models/name, 'https://raw.githubusercontent.com/breizhn/DTLN-aec/master/pretrained_models/'+name, sha)
    license_path=models/'LICENSE-DTLN-AEC.txt'
    if not license_path.exists():
        license_path.write_bytes(urllib.request.urlopen('https://raw.githubusercontent.com/breizhn/DTLN-aec/master/LICENSE',timeout=30).read())
    configuration={
        'aecBackend':'npu', 'npuModelDirectory':str(models),
        'npuRuntimeLibrary':str(runtime/'runtime/lib/intel64/libopenvino_c.so'),
        'libraryPath': ':'.join(str(p) for p in (runtime/'runtime/lib/intel64',runtime/'runtime/3rdparty/tbb/lib',userlib/'usr/lib/x86_64-linux-gnu')),
        'versions': {'openvino':'2026.3.1-ubuntu24','npuDriver':'1.38.0-ubuntu26','levelZero':'1.32.0'},
    }
    (root/'runtime.json').write_text(json.dumps(configuration,indent=2)+'\n')
    if args.probe:
        env=os.environ.copy();env['LD_LIBRARY_PATH']=configuration['libraryPath']+(':'+env['LD_LIBRARY_PATH'] if env.get('LD_LIBRARY_PATH') else '')
        result=subprocess.run([str(args.probe.resolve()),'--probe-npu',str(models),configuration['npuRuntimeLibrary']],env=env,text=True,capture_output=True)
        (root/'probe.json').write_text(result.stdout)
        if result.returncode:
            raise RuntimeError(result.stdout+result.stderr)
        configuration['probe']=json.loads(result.stdout)
    print(json.dumps(configuration,indent=2))

if __name__=='__main__':
    main()
