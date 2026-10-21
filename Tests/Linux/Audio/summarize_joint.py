#!/usr/bin/env python3
"""Correlate completed, real audio and vLLM acceptance windows.

Reads the original measurements without modifying either report. A successful
audio-only soak cannot establish simultaneous GPU/desktop/audio acceptance.
"""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import statistics


def timestamp(value):
    return datetime.fromisoformat(value.replace('Z', '+00:00')).timestamp()


def read_lines(path):
    return [json.loads(line) for line in path.read_text().splitlines() if line.strip()]


def summarize(audio_directory, vllm_directory):
    audio = json.loads((audio_directory / 'result.json').read_text())
    vllm = json.loads((vllm_directory / 'result.json').read_text())
    samples = read_lines(audio_directory / 'samples.jsonl')
    requests = read_lines(vllm_directory / 'requests.jsonl')
    gpus = read_lines(vllm_directory / 'gpu-samples.jsonl')
    failures = []
    start = max(timestamp(audio['measurementStartedAt']), timestamp(vllm['jointStartedAt']))
    finish = min(timestamp(audio['measurementStartedAt']) + samples[-1]['elapsedSeconds'],
                 timestamp(vllm.get('jointCompletedAt') or vllm['failedAt']))
    overlap = max(0, finish - start)
    if overlap < 1800:
        failures.append(f'Common measured interval is only {overlap:.3f} seconds')
    if not audio.get('ok') or audio.get('failures'):
        failures.append('Audio acceptance failed: ' + '; '.join(audio.get('failures', [])))
    if not vllm.get('passed'):
        failures.append('vLLM acceptance failed: ' + vllm.get('error', 'unspecified failure'))
    if not vllm.get('desktopActive'):
        failures.append('Active desktop was not verified')
    if not samples or any(s['activeBackend'] != 'npu' or s['degraded'] for s in samples):
        failures.append('Actual NPU output was not healthy across every audio sample')
    if audio.get('consumption', {}).get('nonfinite') != 0:
        failures.append('Output did not confirm zero nonfinite audio samples')
    intervals = []
    for request in requests:
        # vllm_live records startedAt immediately after response completion;
        # seconds measures the complete request interval ending at that stamp.
        end = timestamp(request['startedAt'])
        intervals.append((end - request['seconds'], end))
    relevant = [(a, b) for a, b in intervals if b >= start and a <= finish]
    if not relevant or relevant[0][0] > start + 2 or relevant[-1][1] < finish - 2:
        failures.append('Successful model requests do not cover the complete audio interval')
    longest_gap = max((a[0] - b[1] for b, a in zip(relevant, relevant[1:])), default=0)
    if longest_gap > 5:
        failures.append(f'Model load has an unmeasured gap of {longest_gap:.3f} seconds')
    common_gpu = [sample for sample in gpus if start <= timestamp(sample['observedAt']) <= finish]
    if len(common_gpu) < int(overlap * .83) or any(not sample['gpu'].get('ok') for sample in common_gpu):
        failures.append('GPU monitoring is missing or unhealthy during the common interval')
    minimum_free = min((sample['gpu']['freeMiB'] for sample in common_gpu
                        if sample['gpu'].get('ok')), default=0)
    if minimum_free < 3072:
        failures.append(f'GPU desktop reserve dropped to {minimum_free} MiB')
    if not audio.get('defaultsUnchanged'):
        failures.append('Original audio default metadata was not preserved')
    first = statistics.median(s['rssKiB'] for s in samples[:min(12, len(samples))])
    last = statistics.median(s['rssKiB'] for s in samples[-min(12, len(samples)):])
    return {
        'observedAt': datetime.now(timezone.utc).isoformat(),
        'ok': not failures,
        'failures': failures,
        'jointVllmLoadValidated': not failures,
        'commonMeasuredSeconds': overlap,
        'audioReport': str((audio_directory / 'result.json').resolve()),
        'vllmReport': str((vllm_directory / 'result.json').resolve()),
        'audioBinarySha256': audio['binarySha256'],
        'modelProfile': vllm['selectedProfile'],
        'successfulRequestsDuringAudio': len(relevant),
        'maximumRequestGapSeconds': max(0, longest_gap),
        'minimumGpuFreeMiBDuringAudio': minimum_free,
        'audioRssKiB': {'firstMinuteMedian': first, 'lastMinuteMedian': last,
                        'change': last-first, 'peak': max(s['rssKiB'] for s in samples)},
        'audio': {'inferences': audio['final']['npu']['inferences'] - audio['baseline']['npu']['inferences'],
                  'newMissingFrames': audio['final']['npu']['missingFrames'] - audio['baseline']['npu']['missingFrames'],
                  'newDroppedPackets': audio['final']['npu']['droppedPackets'] - audio['baseline']['npu']['droppedPackets'],
                  'maximumInferenceMs': audio['final']['npu']['maxInferenceMs'],
                  'maximumCallbackMs': audio['final']['diagnostics']['maxCallbackMs'],
                  'latencyMs': audio['final']['latencyMs'],
                  'nonfiniteOutputSamples': audio['consumption']['nonfinite']},
        'acousticQualityValidated': False,
    }


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--audio', type=Path, required=True)
    parser.add_argument('--vllm', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        report = summarize(args.audio, args.vllm)
    except (KeyError, OSError, ValueError, IndexError) as error:
        report = {'ok': False, 'jointVllmLoadValidated': False,
                  'failures': [f'Acceptance measurements are incomplete: {error}']}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report, indent=2))
    raise SystemExit(0 if report['ok'] else 1)
