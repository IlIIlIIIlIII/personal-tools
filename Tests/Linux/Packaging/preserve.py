#!/usr/bin/env python3
"""Read-only before/after inventory of personal data during package lifecycle QA.

Hashes settings, clipboard, filters and restoration records. NPU/vLLM trees use
file metadata to avoid rereading tens of GiB; metadata equality is not a claim
of byte equality. Reports never include file contents and are written mode 0600.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import stat


def xdg(name, fallback):
    value = os.environ.get(name)
    return Path(value) if value and Path(value).is_absolute() else Path.home()/fallback


def roots():
    config = xdg('XDG_CONFIG_HOME', '.config')
    data = xdg('XDG_DATA_HOME', '.local/share')
    state = xdg('XDG_STATE_HOME', '.local/state')
    paths = {'config': config/'personal-tools', 'data': data/'personal-tools',
             'hdr-restoration': state/'personal-tools/hdr',
             'audio-restoration': state/'personal-tools/audio-routes.json',
             'vllm-unit': config/'systemd/user/personal-tools-vllm.service'}
    settings = config/'personal-tools/clipboard/settings.json'
    if settings.is_file():
        value = json.loads(settings.read_text())
        archive = value.get('ArchivePath') or value.get('archivePath')
        if archive and Path(archive).is_absolute() and Path(archive) != data/'personal-tools/clipboard':
            paths['custom-archive'] = Path(archive)
    return paths


def inventory(paths):
    records = {}
    for name, root in paths.items():
        if not root.exists() and not root.is_symlink():
            records[name] = {'missing': True}
            continue
        candidates = [root, *sorted(root.rglob('*'))] if root.is_dir() and not root.is_symlink() else [root]
        for path in candidates:
            relative = path.relative_to(root)
            key = f'{name}/{relative}'
            value = path.lstat()
            record = {'mode': stat.S_IMODE(value.st_mode), 'uid': value.st_uid, 'gid': value.st_gid,
                      'inode': value.st_ino, 'device': value.st_dev}
            if path.is_symlink():
                record.update(type='symlink', target=os.readlink(path))
            elif path.is_dir():
                record['type'] = 'directory'
            elif path.is_file():
                record.update(type='file', size=value.st_size, mtimeNs=value.st_mtime_ns)
                runtime = name == 'data' and relative.parts and relative.parts[0] in {'npu', 'vllm'}
                if runtime:
                    record['validation'] = 'metadata-only'
                else:
                    with path.open('rb') as source:
                        record['sha256'] = hashlib.file_digest(source, 'sha256').hexdigest()
                    record['validation'] = 'sha256'
            else:
                record['type'] = 'special'
            records[key] = record
    return records


def write_private(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    os.fchmod(descriptor, 0o600)
    with os.fdopen(descriptor, 'w') as target:
        json.dump(value, target, ensure_ascii=False, indent=2)
        target.write('\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['snapshot', 'compare'])
    parser.add_argument('--baseline', type=Path, default=Path('build/linux/package-preservation-before.json'))
    parser.add_argument('--report', type=Path, default=Path('build/linux/package-preservation-after.json'))
    args = parser.parse_args()
    when = datetime.now(timezone.utc).isoformat()
    if args.operation == 'snapshot':
        paths = roots()
        records = inventory(paths)
        value = {'observedAt': when, 'roots': {key: str(path) for key, path in paths.items()}, 'records': records,
                 'note': 'NPU/vLLM: metadata only. Other files: SHA256. No file contents recorded; no user data modified.'}
        write_private(args.baseline, value)
        print(f'Snapshot: {len(records)} records; {args.baseline}')
        return 0
    baseline = json.loads(args.baseline.read_text())
    current = inventory({key: Path(value) for key, value in baseline['roots'].items()})
    previous = baseline['records']
    removed = sorted(previous.keys()-current.keys())
    added = sorted(current.keys()-previous.keys())
    changed = sorted(key for key in previous.keys() & current.keys() if previous[key] != current[key])
    passed = not removed and not added and not changed
    result = {'observedAt': when, 'baselineObservedAt': baseline['observedAt'], 'passed': passed,
              'removed': removed, 'added': added, 'changed': changed,
              'recordCount': len(current), 'note': baseline['note']}
    write_private(args.report, result)
    print(f"{'PASS' if passed else 'FAIL'} preservation; removed={len(removed)} added={len(added)} changed={len(changed)}; {args.report}")
    return 0 if passed else 1


if __name__ == '__main__':
    raise SystemExit(main())
