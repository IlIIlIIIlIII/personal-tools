# Local vLLM runtime

This is a separately owned systemd user service. Closing or explicitly quitting the
Personal Tools window does not stop it. The API binds to `127.0.0.1:8000` and serves
the pinned checkpoint in `manifest.json` as `qwen3.8-27b`.

`python3 runtime.py install --download-model` creates an isolated CPython 3.12
environment, a hash-locked dependency set, the pinned checkpoint, and a stopped
`personal-tools-vllm.service`. It does not alter the system Python, install GPU
drivers, log into external accounts, or start inference during package installation.
The CUDA compiler is pinned to the matching CUDA 13.0 runtime headers because a
newer compiler with older runtime headers fails FlashInfer JIT compilation.

Configuration is stored in `$XDG_CONFIG_HOME/personal-tools/vllm.json` (default
`~/.config/personal-tools/vllm.json`). Supported profiles are 8K, 16K, 32K and 64K,
one active sequence, and 0/2/4/6/8 GiB CPU parameter offload. CPU offload is explicit:
the model still performs CUDA inference, with selected weights moved from RAM.
The running configuration and complete launch command are recorded in
`$XDG_STATE_HOME/personal-tools/vllm/running-config.json`; UI state reads that file
only when its PID matches the managed unit.

The runtime reserves at least 3 GiB of free GPU memory for the desktop and stops
its own server if this reserve is lost. KV cache is bounded for a single sequence;
vLLM independently checks whether the allocation can actually serve the requested
context. A larger requested profile is not reported as validated merely because it
is present in the menu.

From the repository root, hardware acceptance can run durably as a user unit:

```sh
systemd-run --user --unit personal-tools-vllm-acceptance \
  --property=WorkingDirectory="$PWD" \
  /usr/bin/python3 Tests/Linux/Service/vllm_live.py \
  --ladder --seconds 1840 --output build/linux/vllm-qa/new-run \
  --start-signal build/linux/audio-qa/new-run/start-joint.json \
  --audio-progress build/linux/audio-qa/new-run/progress.json
```

Use new output directories for each acceptance window and start the separately
prepared audio acceptance unit first. The test checks its fresh `ready` state and
waits for real
API warm-up, validates Korean/JSON/tool/SSE responses and measured near-limit
token counts, promotes only passing profiles, then signals the common load window.
It records one-second GPU samples, every generation request, the selected profile,
and confirmed stop/restart behavior. Synthetic prompts only are written to evidence.
An existing start signal is rejected, so an old audio interval cannot be mistaken
for a new joint-load run. A failed test is persisted as a failure in `result.json`.

The basic `runtime.py acceptance` command is a short response smoke test; it does
not replace the context ladder, 30-minute overlap, or restart acceptance.
