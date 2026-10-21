#!/usr/bin/env python3
"""Real, restartable local-model acceptance; run in a systemd user unit.

All prompts are synthetic. No clipboard, user documents or credentials enter reports.
"""
import argparse
import datetime
import importlib.util
import json
import pathlib
import subprocess
import sys
import threading
import time
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location('owned_vllm', ROOT / 'Sources/Linux/Vllm/runtime.py')
runtime = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime)
MODEL = runtime.MANIFEST['servedModel']


def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def service(action):
    subprocess.run(['systemctl', '--user', action, 'personal-tools-vllm.service'], check=True, timeout=45)


def running():
    try:
        return json.loads((runtime.STATE / 'status.json').read_text())
    except (FileNotFoundError, json.JSONDecodeError):
        return {}


def wait_ready(timeout=1200):
    start = time.monotonic()
    while time.monotonic() - start < timeout:
        status = running()
        active = subprocess.run(['systemctl', '--user', 'is-active', '--quiet', 'personal-tools-vllm.service']).returncode == 0
        if status.get('status') == 'running' and active and runtime.model_ready():
            return time.monotonic() - start
        if not active:
            raise RuntimeError(status.get('error') or 'Owned vLLM service is not active')
        time.sleep(2)
    raise RuntimeError('Owned vLLM did not become ready before timeout')


def chat(prompt, max_tokens=128, timeout=600, **extra):
    return runtime.http_json('/v1/chat/completions', {
        'model': MODEL, 'messages': [{'role': 'user', 'content': prompt}],
        'max_tokens': max_tokens, 'temperature': 0,
        'chat_template_kwargs': {'enable_thinking': False}, **extra}, timeout=timeout)


def content(reply):
    return reply['choices'][0]['message'].get('content') or ''


def near_limit_prompt(context):
    # Token count is measured by the running model's own chat template/tokenizer.
    prefix = 'Remember this exact secret marker: OLIVE-7264.\nIgnore the following filler.\n'
    suffix = '\nWhat is the secret marker from the beginning? Reply with the marker only.'
    def measure(n):
        prompt = prefix + ' meadow' * n + suffix
        tokens = runtime.http_json('/tokenize', {'model': MODEL, 'messages': [{'role': 'user', 'content': prompt}], 'chat_template_kwargs': {'enable_thinking': False}}, timeout=60)['count']
        return prompt, tokens
    # Word pieces are model-dependent (" meadow" is two tokens here).
    # Search the measured length instead of assuming a word/token ratio.
    low, high = 1, context * 2
    while low <= high:
        count = (low + high) // 2
        prompt, tokens = measure(count)
        if context - 384 <= tokens <= context - 128:
            break
        if tokens < context - 384:
            low = count + 1
        else:
            high = count - 1
    if not context - 512 <= tokens <= context - 128:
        raise RuntimeError(f'Near-limit token count {tokens} does not exercise profile {context}')
    return prompt, tokens


def profile_tests(context):
    results = []
    start = time.monotonic()
    korean = chat('한국어로 2 더하기 3의 답을 숫자 하나로만 말하세요.', 16)
    if content(korean).strip().strip('.') != '5':
        raise RuntimeError('Korean arithmetic semantic check failed')
    results.append({'name': 'korean', 'seconds': time.monotonic()-start, 'usage': korean.get('usage'), 'content': content(korean)})
    start = time.monotonic()
    structured = chat('Return JSON with a boolean field ok set to true.', response_format={'type': 'json_object'})
    if json.loads(content(structured)).get('ok') is not True:
        raise RuntimeError('Structured JSON semantic check failed')
    results.append({'name': 'json', 'seconds': time.monotonic()-start, 'usage': structured.get('usage')})
    start = time.monotonic()
    tool = chat('Call get_weather for Seoul. Do not answer without the tool.', tools=[{'type': 'function', 'function': {'name': 'get_weather', 'description': 'Get weather for a city', 'parameters': {'type': 'object', 'properties': {'city': {'type': 'string'}}, 'required': ['city']}}}], tool_choice='required')
    calls = tool['choices'][0]['message'].get('tool_calls') or []
    if not calls or calls[0]['function']['name'] != 'get_weather' or 'seoul' not in json.loads(calls[0]['function']['arguments']).get('city', '').lower():
        raise RuntimeError('Tool call schema/argument check failed')
    results.append({'name': 'tool-call', 'seconds': time.monotonic()-start, 'usage': tool.get('usage')})
    start = time.monotonic()
    payload = {'model': MODEL, 'messages': [{'role': 'user', 'content': 'Reply exactly STREAM_OK.'}], 'max_tokens': 32, 'temperature': 0, 'stream': True, 'chat_template_kwargs': {'enable_thinking': False}}
    request = urllib.request.Request(runtime.BASE + '/v1/chat/completions', json.dumps(payload).encode(), {'Content-Type': 'application/json'})
    chunks, text, done = 0, '', False
    with urllib.request.urlopen(request, timeout=180) as response:
        for line in response:
            if not line.startswith(b'data: '):
                continue
            data = line[6:].strip()
            if data == b'[DONE]':
                done = True
                break
            part = json.loads(data)
            for choice in part.get('choices', []):
                delta = choice.get('delta', {}).get('content') or ''
                if delta:
                    chunks += 1
                    text += delta
    if not done or 'STREAM_OK' not in text or chunks < 1:
        raise RuntimeError('SSE stream framing or content failed')
    results.append({'name': 'streaming', 'seconds': time.monotonic()-start, 'chunks': chunks, 'done': done})
    prompt, tokens = near_limit_prompt(context)
    start = time.monotonic()
    long = chat(prompt, 64, timeout=1800)
    if 'OLIVE-7264' not in content(long):
        raise RuntimeError(f'{context}-token profile failed beginning-marker retrieval')
    results.append({'name': 'near-context-limit', 'seconds': time.monotonic()-start, 'inputTokens': tokens, 'usage': long.get('usage'), 'content': content(long)})
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    parser.add_argument('--start-signal', type=pathlib.Path)
    parser.add_argument('--audio-progress', type=pathlib.Path)
    parser.add_argument('--seconds', type=int, default=1830)
    parser.add_argument('--ladder', action='store_true')
    parser.add_argument('--resume', action='store_true')
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    report = {'startedAt': now(), 'manifest': runtime.MANIFEST, 'profiles': [], 'passed': False}
    if args.resume and (output / 'result.json').exists():
        previous = json.loads((output / 'result.json').read_text())
        if previous['manifest'] != runtime.MANIFEST or 'jointStartedAt' in previous:
            raise RuntimeError('Only the same manifest before a joint window can resume profile checks')
        report.update(startedAt=previous['startedAt'], resumedAt=now(), profiles=[p for p in previous['profiles'] if p['passed']])
    stop_monitor = threading.Event()
    samples = []
    def save():
        runtime.atomic_json(output / 'result.json', report)
    def progress(stage, **extra):
        runtime.atomic_json(output / 'progress.json', {'observedAt': now(), 'stage': stage, **extra})
    def monitor():
        with (output / 'gpu-samples.jsonl').open('w', buffering=1) as log:
            while not stop_monitor.is_set():
                sample = {'observedAt': now(), 'gpu': runtime.gpu_probe(), 'runtime': running().get('status')}
                samples.append(sample)
                log.write(json.dumps(sample) + '\n')
                stop_monitor.wait(1)
    thread = threading.Thread(target=monitor, daemon=True)
    thread.start()
    try:
        wait_ready()
        config = runtime.read_config()
        best = max((p['context'] for p in report['profiles']), default=None)
        ladder = [n for n in runtime.MANIFEST['contextLadder'] if best is None or n>best] if args.ladder else [config['context']]
        for context in ladder:
            profile = {'context': context, 'cpuOffloadGiB': config['cpuOffloadGiB'], 'startedAt': now(), 'passed': False}
            report['profiles'].append(profile)
            save()
            progress('profile', context=context)
            try:
                if runtime.read_config()['context'] != context:
                    service('stop')
                    runtime.atomic_json(runtime.CONFIG, {**config, 'context': context})
                    service('start')
                    profile['warmupSeconds'] = wait_ready()
                profile['tests'] = profile_tests(context)
                profile['gpu'] = runtime.gpu_probe()
                if not profile['gpu']['ok'] or profile['gpu']['freeMiB'] < runtime.MANIFEST['desktopReserveMiB']:
                    raise RuntimeError('Required 3 GiB desktop reserve did not survive profile tests')
                profile['passed'] = True
                best = context
                profile['completedAt'] = now()
                save()
            except Exception as error:
                profile['error'] = str(error)
                profile['runtimeStatusOnFailure'] = running()
                report['unattemptedProfiles'] = [n for n in ladder if n>context]
                save()
                if best is None:
                    raise
                break
        if best is None:
            raise RuntimeError('No context profile passed')
        if runtime.read_config()['context'] != best or running().get('status') != 'running':
            service('stop')
            runtime.atomic_json(runtime.CONFIG, {**config, 'context': best})
            service('start')
            wait_ready()
        report['selectedProfile'] = runtime.read_config()
        if args.audio_progress:
            deadline = time.monotonic()+1800
            progress('waiting-for-audio', context=best)
            while time.monotonic() < deadline:
                try:
                    audio = json.loads(args.audio_progress.read_text())
                except (FileNotFoundError, json.JSONDecodeError):
                    audio = {}
                if audio.get('phase') == 'ready':
                    observed = datetime.datetime.fromisoformat(audio['observedAt'])
                    if (datetime.datetime.now(datetime.timezone.utc)-observed).total_seconds() < 20:
                        break
                if audio.get('phase') == 'failed':
                    raise RuntimeError('Audio acceptance failed before joint-load start')
                time.sleep(2)
            else:
                raise RuntimeError('Audio did not become ready for the common measurement window')
        report['jointStartedAt'] = now()
        report['jointRequiredSeconds'] = args.seconds
        report['desktopActive'] = subprocess.run(['pgrep', '-x', 'gnome-shell'], capture_output=True).returncode == 0
        if not report['desktopActive']:
            raise RuntimeError('GNOME desktop is not active during coexistence test')
        if args.start_signal:
            if args.start_signal.exists():
                raise RuntimeError('Joint-load start signal already exists; refusing to reuse an old acceptance window')
            runtime.atomic_json(args.start_signal, {'startedAt': report['jointStartedAt'], 'service': 'personal-tools-vllm.service', 'model': MODEL, 'revision': runtime.MANIFEST['revision'], 'profile': report['selectedProfile'], 'resultPath': str(output / 'result.json')})
        save()
        started = time.monotonic()
        requests = 0
        min_free = 2**31
        with (output / 'requests.jsonl').open('w', buffering=1) as log:
            while time.monotonic()-started < args.seconds:
                start = time.monotonic()
                # Alternate substantial uncached prefill with structured generation.
                prompt = f'Test request {requests}. Ignore filler and list the numbers 1 through 30, one per line.\n' + (' meadow' * (3072 if requests % 3 == 0 else 64))
                response = chat(prompt, 160)
                if not content(response):
                    raise RuntimeError('Empty response during sustained joint load')
                gpu = runtime.gpu_probe()
                if not gpu['ok']:
                    raise RuntimeError(gpu['error'])
                min_free = min(min_free, gpu['freeMiB'])
                if min_free < runtime.MANIFEST['desktopReserveMiB']:
                    raise RuntimeError('Desktop reserve dropped below 3 GiB during joint load')
                requests += 1
                sample = {'request': requests, 'startedAt': now(), 'elapsedSeconds': time.monotonic()-started, 'seconds': time.monotonic()-start, 'usage': response.get('usage'), 'gpuFreeMiB': gpu['freeMiB']}
                log.write(json.dumps(sample) + '\n')
                progress('joint-load', elapsedSeconds=sample['elapsedSeconds'], requests=requests, minFreeMiB=min_free, context=best)
        report['jointCompletedAt'] = now()
        report['jointElapsedSeconds'] = time.monotonic()-started
        report['jointRequests'] = requests
        report['jointPostRequestMinFreeMiB'] = min_free
        joint_samples = [s for s in samples if report['jointStartedAt'] <= s['observedAt'] <= report['jointCompletedAt']]
        report['jointMinFreeMiB'] = min((s['gpu']['freeMiB'] for s in joint_samples if s['gpu'].get('ok')), default=0)
        if any(not s['gpu'].get('ok') for s in joint_samples) or report['jointMinFreeMiB'] < runtime.MANIFEST['desktopReserveMiB']:
            raise RuntimeError('Continuous GPU sampling did not preserve the 3 GiB desktop reserve')
        # Stop and restart the owned service, proving port release and subsequent readiness.
        progress('stop-restart')
        service('stop')
        if runtime.model_ready():
            raise RuntimeError('Owned model remained reachable after systemd stop')
        report['stopConfirmed'] = True
        service('start')
        report['restartWarmupSeconds'] = wait_ready()
        if content(chat('Reply only RESTART_OK.', 32)).strip().strip('.') != 'RESTART_OK':
            raise RuntimeError('Restarted service generation failed')
        report['passed'] = True
        report['completedAt'] = now()
        progress('complete', passed=True, context=best)
    except Exception as error:
        report['error'] = str(error)
        report['failedAt'] = now()
        progress('failed', error=str(error))
        print(str(error), file=sys.stderr)
    finally:
        stop_monitor.set()
        thread.join(timeout=15)
        report['gpuSamples'] = len(samples)
        report['minObservedFreeMiB'] = min((s['gpu']['freeMiB'] for s in samples if s['gpu'].get('ok')), default=None)
        save()
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    sys.exit(main())
