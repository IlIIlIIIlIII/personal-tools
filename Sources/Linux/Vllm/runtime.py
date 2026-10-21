#!/usr/bin/env python3
"""Owned, loopback vLLM user service. No simulated completion or automatic driver changes."""
from __future__ import annotations
import argparse, datetime, fcntl, hashlib, json, os, pathlib, shutil, signal, socket, subprocess, sys, tarfile, tempfile, time, urllib.error, urllib.request
HERE = pathlib.Path(__file__).resolve().parent
MANIFEST = json.loads((HERE / "manifest.json").read_text())
HOME = pathlib.Path.home()
def xdg(name, fallback):
    value = os.environ.get(name)
    if value and not os.path.isabs(value):
        raise RuntimeError(f"{name} must be absolute")
    return pathlib.Path(value or HOME / fallback) / "personal-tools"
DATA = xdg("XDG_DATA_HOME", ".local/share") / "vllm"
CONFIG = xdg("XDG_CONFIG_HOME", ".config") / "vllm.json"
STATE = xdg("XDG_STATE_HOME", ".local/state") / "vllm"
VENV = DATA / "venv"
BASE = "http://127.0.0.1:8000"
def atomic_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    temporary = path.with_suffix(".tmp")
    with open(temporary, "w", encoding="utf-8") as output:
        os.chmod(temporary, 0o600)
        json.dump(value, output, ensure_ascii=False, indent=2); output.flush(); os.fsync(output.fileno())
    os.replace(temporary, path)
def read_config():
    config = {"context": 8192, "maxSequences": 1, "cpuOffloadGiB": 0}
    if CONFIG.exists(): config.update(json.loads(CONFIG.read_text()))
    if config["context"] not in MANIFEST["contextLadder"] or config["maxSequences"] != 1 or config["cpuOffloadGiB"] not in MANIFEST["cpuOffloadGiBLadder"]:
        raise ValueError("Only measured context ladder, single sequence and 0/2/4/6/8 GiB offload profiles are accepted")
    return config

def gpu_probe():
    try:
        result = subprocess.run(["nvidia-smi", "--query-gpu=index,name,memory.total,memory.free,driver_version", "--format=csv,noheader,nounits"], text=True, capture_output=True, timeout=10)
    except (OSError, subprocess.TimeoutExpired) as error:
        return {"ok": False, "error": str(error)}
    if result.returncode:
        return {"ok": False, "error": (result.stderr + result.stdout).strip()[:2000]}
    rows = result.stdout.strip().splitlines()
    if len(rows) != 1:
        return {"ok": False, "error": "This desktop profile requires exactly one explicit NVIDIA GPU; automatic GPU selection is disabled."}
    index, name, total, free, driver = [p.strip() for p in rows[0].split(",")]
    return {"ok": True, "index": int(index), "name": name, "totalMiB": int(total), "freeMiB": int(free), "driver": driver}

def http_json(path, payload=None, timeout=8):
    body = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request(BASE + path, body, {"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.load(response)

def model_ready():
    try:
        data = http_json("/v1/models")
        return any(item.get("id") == MANIFEST["servedModel"] for item in data.get("data", []))
    except (OSError, ValueError):
        return False

def assert_port_available(host="127.0.0.1", number=8000):
    with socket.socket() as port:
        # Completed connections in TIME_WAIT are not a live foreign listener.
        port.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try: port.bind((host, number))
        except OSError as error: raise RuntimeError(f"Port {number} is occupied; refusing to adopt or stop an unowned server") from error

def cuda_environment():
    # NVIDIA's CUDA 13 wheels use lib/, while FlashInfer JIT expects a toolkit
    # lib64/ tree and the linker name libcudart.so. Keep aliases inside this venv.
    cuda = VENV / "lib/python3.12/site-packages/nvidia/cu13"
    if not (cuda / "bin/nvcc").is_file(): raise RuntimeError("The isolated CUDA compiler package is missing; rerun runtime.py install")
    if not (cuda / "lib64").exists(): (cuda / "lib64").symlink_to("lib", target_is_directory=True)
    if not (cuda / "lib/libcudart.so").exists(): (cuda / "lib/libcudart.so").symlink_to("libcudart.so.13")
    return {"CUDA_HOME":str(cuda),"PATH":str(cuda / "bin")+os.pathsep+os.environ.get("PATH","")}

def command(config, gpu):
    # vLLM's utilization is a total-device fraction. Account for existing desktop allocations AND reserve.
    # vLLM's profiled allocation excludes some driver/JIT memory. Keep an extra
    # GiB so the measured desktop reserve survives warmup and later requests.
    available = gpu["freeMiB"] - MANIFEST["desktopReserveMiB"] - 1024
    if available <= 0:
        raise RuntimeError("Less than 3 GiB desktop reserve is available before starting vLLM")
    utilization = min(0.85, available / gpu["totalMiB"])
    # A single sequence needs bounded cache capacity. Letting utilization consume
    # every spare byte created 4x excess concurrency capacity and left no space
    # for CUDA/FlashInfer's later warm-up allocations on this desktop GPU.
    # This bound includes hybrid-attention page rounding for the pinned model;
    # vLLM still validates the actual minimum against max-model-len at startup.
    cache_bytes = config["context"] * 32 * 1024 + 512 * 1024 * 1024
    model_path = DATA / "model"
    installed = model_path / "installed-checkpoint.json"
    if not installed.exists() or json.loads(installed.read_text()) != {"checkpoint": MANIFEST["checkpoint"], "revision": MANIFEST["revision"]}:
        raise RuntimeError("Pinned checkpoint is not installed; run runtime.py install --download-model")
    return [str(VENV / "bin/vllm"), "serve", str(model_path), "--served-model-name", MANIFEST["servedModel"],
            "--host", "127.0.0.1", "--port", "8000", "--max-model-len", str(config["context"]),
            "--tensor-parallel-size", "1", "--max-num-seqs", "1", "--max-num-batched-tokens", "512",
            "--quantization", "compressed-tensors", "--kv-cache-dtype", "fp8", "--enforce-eager",
            "--gpu-memory-utilization", f"{utilization:.6f}", "--kv-cache-memory-bytes", str(cache_bytes), "--cpu-offload-gb", str(config["cpuOffloadGiB"]),
            "--language-model-only", "--enable-auto-tool-choice", "--tool-call-parser", "qwen3_xml", "--reasoning-parser", "qwen3"]

def runtime():
    STATE.mkdir(parents=True, exist_ok=True, mode=0o700)
    lock = open(STATE / "run.lock", "a")
    try: fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        print("The owned vLLM runtime is already active", file=sys.stderr); return 1
    started = time.monotonic(); steps = []
    def publish(status, error=None, gpu=None):
        atomic_json(STATE / "status.json", {"status": status, "error": error, "steps": steps, "elapsedSeconds": int(time.monotonic()-started),
                    "gpu": (gpu or {}).get("name", ""), "observedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(), "pid": os.getpid()})
    def step(name, label, state, detail=None):
        item = {"id": name, "label": label, "state": state}
        if detail: item["detail"] = detail
        previous = next((s for s in steps if s["id"] == name), None)
        if previous: previous.update(item)
        else: steps.append(item)
    process = None
    try:
        step("gpu", "GPU / 드라이버 확인", "running"); publish("starting")
        gpu = gpu_probe()
        if not gpu["ok"]: raise RuntimeError(gpu["error"])
        step("gpu", "GPU / 드라이버 확인", "done", gpu["name"])
        assert_port_available()
        config = read_config(); args = command(config, gpu)
        atomic_json(STATE / "running-config.json", {"config": config,"manifest": MANIFEST,"command": args,"pid": os.getpid(),"startedAt": datetime.datetime.now(datetime.timezone.utc).isoformat()})
        if not pathlib.Path(args[0]).is_file(): raise RuntimeError("Python 3.12 vLLM environment is not installed")
        step("load", "고정된 체크포인트 로드", "running", MANIFEST["checkpoint"])
        step("health", "HTTP 모델 / 생성 확인", "pending"); publish("starting", gpu=gpu)
        with open(STATE / "server.log", "a", buffering=1) as log:
            os.chmod(STATE / "server.log", 0o600)
            environment = dict(os.environ, VLLM_NO_USAGE_STATS="1", DO_NOT_TRACK="1", HF_HUB_OFFLINE="1", HF_HOME=str(DATA / "hf-cache"))
            environment.update(cuda_environment())
            process = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT, env=environment, start_new_session=True)
            def stop(_signum, _frame):
                if process.poll() is None: os.killpg(process.pid, signal.SIGTERM)
            signal.signal(signal.SIGTERM, stop); signal.signal(signal.SIGINT, stop)
            ready = False
            while process.poll() is None:
                gpu_now = gpu_probe()
                if not gpu_now["ok"] or gpu_now["freeMiB"] < MANIFEST["desktopReserveMiB"]:
                    raise RuntimeError(gpu_now.get("error") or f"Desktop reserve dropped below 3072 MiB (observed {gpu_now['freeMiB']} MiB); stopping owned vLLM service")
                if not ready and model_ready():
                    step("load", "고정된 체크포인트 로드", "done")
                    step("health", "HTTP 모델 / 생성 확인", "running"); publish("starting", gpu=gpu)
                    reply = http_json("/v1/chat/completions", {"model": MANIFEST["servedModel"], "messages": [{"role": "user", "content": "Reply only OK."}], "max_tokens": 16, "temperature": 0, "chat_template_kwargs": {"enable_thinking": False}}, timeout=120)
                    if not reply.get("choices"): raise RuntimeError("Generation returned no choices")
                    step("health", "HTTP 모델 / 생성 확인", "done"); ready = True
                if not ready and time.monotonic()-started > 1200: raise RuntimeError("Model readiness timed out after 20 minutes; inspect server.log")
                publish("running" if ready else "starting", gpu=gpu); time.sleep(2)
            if process.returncode not in [0, -signal.SIGTERM]: raise RuntimeError(f"vLLM exited {process.returncode}; inspect server.log")
            publish("stopped", gpu=gpu)
    except Exception as error:
        for item in steps:
            if item["state"] == "running": item["state"] = "error"; item["detail"] = str(error)
        publish("error", str(error)); print(str(error), file=sys.stderr)
        return 1
    finally:
        if process and process.poll() is None:
            os.killpg(process.pid, signal.SIGTERM)
            try: process.wait(timeout=20)
            except subprocess.TimeoutExpired: os.killpg(process.pid, signal.SIGKILL); process.wait()
        lock.close()
    return 0

def install(download_model=False):
    """Download pinned uv + standalone Python, lock dependencies, install; never touches system Python."""
    DATA.mkdir(parents=True, exist_ok=True, mode=0o700); STATE.mkdir(parents=True, exist_ok=True, mode=0o700)
    if os.uname().machine != "x86_64": raise RuntimeError("This manifest targets Ubuntu x86_64")
    uv = DATA / "tools/uv"
    if not uv.exists():
        asset = "uv-x86_64-unknown-linux-gnu.tar.gz"
        base = f"https://github.com/astral-sh/uv/releases/download/{MANIFEST['uv']}/"
        with tempfile.TemporaryDirectory(prefix="personal-tools-uv-") as temporary:
            archive = pathlib.Path(temporary) / asset
            urllib.request.urlretrieve(base + asset, archive)
            expected = urllib.request.urlopen(base + asset + ".sha256").read().decode().split()[0]
            if hashlib.sha256(archive.read_bytes()).hexdigest() != expected: raise RuntimeError("uv download checksum mismatch")
            with tarfile.open(archive) as source: source.extractall(temporary, filter="data")
            uv.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(pathlib.Path(temporary) / "uv-x86_64-unknown-linux-gnu/uv", uv); uv.chmod(0o700)
    environment = dict(os.environ, UV_PYTHON_INSTALL_DIR=str(DATA / "python"), UV_CACHE_DIR=str(DATA / "uv-cache"))
    def run(*args): subprocess.run([str(uv), *map(str,args)], env=environment, check=True)
    run("python", "install", MANIFEST["python"])
    if not (VENV / "bin/python").exists(): run("venv", "--python", MANIFEST["python"], VENV)
    requirements = DATA / "requirements.in"
    requirements.write_text(f"vllm=={MANIFEST['vllm']}\nhuggingface-hub=={MANIFEST['huggingfaceHub']}\n"+"".join(f"{name}=={MANIFEST['cudaCompiler']}\n" for name in ["nvidia-cuda-nvcc","nvidia-cuda-crt","nvidia-nvvm"]))
    lock = DATA / "requirements.lock"
    manifest_path = DATA / "installed-manifest.json"
    previous = json.loads(manifest_path.read_text()) if manifest_path.exists() else {}
    if not lock.exists() or any(previous.get(key)!=MANIFEST[key] for key in ["python","vllm","huggingfaceHub","cudaCompiler"]): run("pip", "compile", "--python", VENV / "bin/python", "--generate-hashes", "--output-file", lock, requirements)
    run("pip", "sync", "--python", VENV / "bin/python", "--require-hashes", lock)
    resolved = subprocess.check_output([str(uv), "pip", "freeze", "--python", str(VENV / "bin/python")], text=True, env=environment)
    (DATA / "installed-requirements.txt").write_text(resolved)
    atomic_json(DATA / "installed-manifest.json", {**MANIFEST, "installedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(), "weightsInstalled": (DATA / "model/installed-checkpoint.json").exists()})
    if download_model:
        subprocess.run([str(VENV / "bin/hf"), "download", MANIFEST["checkpoint"], "--revision", MANIFEST["revision"], "--local-dir", str(DATA / "model")], check=True)
        # Record completion only after hf has validated all downloads.
        atomic_json(DATA / "model/installed-checkpoint.json", {"checkpoint": MANIFEST["checkpoint"], "revision": MANIFEST["revision"]})
    unit_directory = pathlib.Path(os.environ.get("XDG_CONFIG_HOME", HOME / ".config")) / "systemd/user"
    unit_directory.mkdir(parents=True, exist_ok=True)
    # Installed script paths are fixed when packaging; source-checkout invocation is also supported.
    def quote(value): return '"' + str(value).replace('\\','\\\\').replace('"','\\"').replace('%','%%') + '"'
    unit = "[Unit]\nDescription=Personal Tools local Qwen3.8 server\nAfter=network-online.target\n\n[Service]\nType=simple\nExecStart=" + quote(sys.executable) + " " + quote(HERE / "runtime.py") + " run\nKillMode=control-group\nTimeoutStopSec=30\nRestart=no\nUMask=0077\n\n[Install]\nWantedBy=default.target\n"
    (unit_directory / "personal-tools-vllm.service").write_text(unit)
    subprocess.run(["systemctl", "--user", "daemon-reload"], check=True)
    print("Installed. Service remains stopped. Start through Personal Tools after GPU probe succeeds.")

def acceptance():
    """Measure the CURRENT profile. Profile promotion is explicit; no false 64K success."""
    gpu = gpu_probe()
    if not gpu["ok"]: raise RuntimeError(gpu["error"])
    if not model_ready(): raise RuntimeError("Pinned model is not healthy")
    report = {"config": read_config(), "manifest": MANIFEST, "startedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(), "tests": [], "minFreeMiB": gpu["freeMiB"]}
    for name, prompt, extra in [("korean", "한국어로 2 더하기 3의 답만 말하세요.", {}), ("code", "Return a Python function add(a,b) that returns the sum.", {}), ("json", 'Return JSON with a boolean field ok set to true.', {"response_format": {"type": "json_object"}})]:
        start = time.monotonic()
        response = http_json("/v1/chat/completions", {"model": MANIFEST["servedModel"], "messages": [{"role": "user", "content": prompt}], "max_tokens": 128, "temperature": 0, "chat_template_kwargs": {"enable_thinking": False}, **extra}, timeout=180)
        text = response["choices"][0]["message"].get("content", "")
        if not text: raise RuntimeError(f"{name}: empty response")
        if name == "json" and json.loads(text).get("ok") is not True: raise RuntimeError("JSON schema semantic check failed")
        sample = gpu_probe()
        if not sample["ok"]: raise RuntimeError(sample["error"])
        report["minFreeMiB"] = min(report["minFreeMiB"], sample["freeMiB"])
        report["tests"].append({"id": name, "seconds": time.monotonic()-start, "usage": response.get("usage"), "content": text})
    report["passed"] = report["minFreeMiB"] >= MANIFEST["desktopReserveMiB"]
    report["scope"] = "Basic generation only. Long-context, streaming, tool-calling, MTP and 30-minute desktop coexistence remain separate required gates."
    atomic_json(STATE / "acceptance.json", report)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    if not report["passed"]: raise RuntimeError("Desktop VRAM reserve failed")

def main():
    parser = argparse.ArgumentParser(description=__doc__); parser.add_argument("command", choices=["probe", "install", "run", "acceptance"]); parser.add_argument("--download-model", action="store_true"); args = parser.parse_args()
    try:
        if args.command == "probe":
            result = gpu_probe(); print(json.dumps(result, ensure_ascii=False)); return 0 if result["ok"] else 2
        if args.command == "install": install(args.download_model)
        elif args.command == "run": return runtime()
        elif args.command == "acceptance": acceptance()
        return 0
    except Exception as error: print(str(error), file=sys.stderr); return 1
if __name__ == "__main__": sys.exit(main())
