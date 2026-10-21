"""Inspect bounded AEC traces; optionally export the last <=120 seconds as float WAV.

No third-party dependencies. WAV channels: raw mic, processed mic (latency aligned),
speaker left, speaker right. Missing diagnostic packets remain zero; do not mistake
them for recorded silence. Binary originals retain exact timestamps and gaps.
"""
import argparse
import array
import collections
import datetime
import json
import pathlib
import struct
import ctypes
import time as timer

HEADER = struct.Struct('<4I7Q')
MAGIC = 0x31444341


def records(paths):
    for path in paths:
        with path.open('rb') as f:
            while True:
                raw = f.read(HEADER.size)
                if len(raw) != HEADER.size:
                    break
                h = HEADER.unpack(raw)
                if h[0] != MAGIC:
                    break  # Reserved, not-yet-written tail.
                payload = b''
                if h[1] in (1, 2, 12):
                    if not 0 < h[2] <= 256:
                        raise ValueError(f'Invalid frame count in {path}')
                    payload = f.read(h[2]*8)
                    if len(payload) != h[2]*8:
                        break
                yield h, payload


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('directory', type=pathlib.Path)
    p.add_argument('--session', help='File prefix aec-PID-SESSION (default: latest session)')
    p.add_argument('--wav-seconds', type=int, default=0, choices=range(121), metavar='0..120')
    args = p.parse_args()
    files = list(args.directory.glob('aec-*.acd'))
    if not files:
        raise SystemExit('No diagnostic segments yet; reopen the microphone after enabling.')
    session = args.session or max(files, key=lambda x: x.stat().st_mtime_ns).stem.rsplit('-', 1)[0]
    files = sorted((x for x in files if x.stem.rsplit('-', 1)[0] == session), key=lambda x: int(x.stem.rsplit('-', 1)[1]))
    counts = collections.Counter()
    errors = collections.deque(maxlen=20)
    states = collections.Counter()
    missing = collections.deque(maxlen=100)
    resets = collections.deque(maxlen=100)
    missing_total = reset_count = 0
    unavailable = 0
    infer_max_ms = 0
    queue_max = [0, 0]
    diag_drops = [0, 0, 0]
    audio_queue_drops = [0, 0]
    frequency, anchor_qpc, anchor_ft, last_time = 0, 0, 0, 0
    for h, payload in records(files):
        _, kind, frames, flags, qpc, time, index, a, b, c, d = h
        counts[kind] += 1
        if kind == 12:
            errors.append(payload.split(b'\0', 1)[0].decode('utf-8', errors='replace'))
        if kind == 9:
            states[str(a if a != 2**64-1 else -1)] += 1
        if kind == 6:
            frequency, anchor_qpc, anchor_ft = a, qpc, b
        elif kind == 1:
            last_time = max(last_time, time + frames*10_000_000//48000)
            audio_queue_drops = [max(audio_queue_drops[0], b), max(audio_queue_drops[1], c)]
        elif kind == 3:
            unavailable += c == 0
            queue_max = [max(queue_max[0], d >> 32), max(queue_max[1], d & 0xffffffff)]
        elif kind == 4 and frequency:
            infer_max_ms = max(infer_max_ms, (b-a)*1000/frequency)
        elif kind == 5:
            reset_count += 1
            resets.append({'qpc': qpc, 'index': index, 'reason_bits': flags, 'drift_100ns': b, 'queue_losses': c})
        elif kind == 7:
            diag_drops = [max(diag_drops[0], a), max(diag_drops[1], b), max(diag_drops[2], c)]
        elif kind == 8:
            missing_total += a
            utc = (anchor_ft-116444736000000000)/10_000_000 + (qpc-anchor_qpc)/frequency if frequency else 0
            missing.append({'utc': datetime.datetime.fromtimestamp(utc, datetime.timezone.utc).isoformat(), 'qpc': qpc, 'frames': a, 'first_target': b, 'observed_stamp': c, 'last_published_exclusive': d})
    print(json.dumps({'session': session, 'segments': len(files), 'record_counts': dict(counts), 'errors': list(errors), 'states': dict(states),
                      'missing_frames': missing_total, 'missing_events': list(missing),
                      'reference_unavailable_packets': unavailable, 'max_inference_ms': infer_max_ms,
                      'max_queue_depth_mic_ref': queue_max, 'audio_queue_drops_mic_ref': audio_queue_drops,
                      'diagnostic_drops_mic_ref_worker': diag_drops, 'reset_count': reset_count, 'resets': list(resets)}, indent=2))
    if not args.wav_seconds or not last_time:
        return
    frames = args.wav_seconds*48000
    size = frames*16+44
    # Logs reserve <=14 GB, exports share a 15 GB hard limit with all other data.
    start = last_time-args.wav_seconds*10_000_000
    audio = array.array('f', [0])*(frames*4)
    for h, payload in records(files):
        if h[1] not in (1, 2):
            continue
        samples = array.array('f'); samples.frombytes(payload)
        base = round((h[5]-start)*48000/10_000_000)
        for i in range(h[2]):
            n = base+i
            if h[1] == 1:
                if 0 <= n < frames:
                    audio[n*4] = samples[i*2]
                if 0 <= n-4096 < frames:
                    audio[(n-4096)*4+1] = samples[i*2+1]
            elif 0 <= n < frames:
                audio[n*4+2] = samples[i*2]; audio[n*4+3] = samples[i*2+1]
    target = args.directory/(session+'-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S')+'.wav')
    # Share the recorder's reservation lock; release immediately after allocation.
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
    kernel.CreateFileW.restype = ctypes.c_void_p
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    invalid = ctypes.c_void_p(-1).value
    lock = invalid
    for _ in range(100):
        lock = kernel.CreateFileW(str(args.directory/'budget.lock'), 0xc0000000, 0, None, 4, 128, None)
        if lock != invalid:
            break
        timer.sleep(.02)
    if lock == invalid:
        raise SystemExit('Cannot acquire diagnostic budget lock; export refused.')
    try:
        used = sum(x.stat().st_size for x in args.directory.rglob('*') if x.is_file())
        if used+size > 14_990_000_000:
            raise SystemExit('15 GB diagnostic budget would be exceeded; export refused.')
        f = target.open('xb')
        try:
            f.truncate(size)
        except BaseException:
            f.close()
            raise
    finally:
        kernel.CloseHandle(lock)
    with f:
        f.write(struct.pack('<4sI4s4sIHHIIHH4sI', b'RIFF', size-8, b'WAVE', b'fmt ', 16, 3, 4, 48000, 768000, 16, 32, b'data', frames*16))
        audio.tofile(f)
    print(f'WAV: {target}; start host time={start}; final 4096 processed frames unavailable by construction.')


if __name__ == '__main__':
    main()
