#!/usr/bin/env bash
set -euo pipefail
# runtime.json comes from linux-audio-prepare-npu.py; no global environment edits.
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_dir=$(dirname -- "$script_dir")
audio_binary=${PERSONALTOOLS_AUDIO_BINARY:-"$repo_dir/build/linux/native/Sources/Linux/Audio/PersonalToolsAudio"}
runtime_json=${PERSONALTOOLS_NPU_CONFIG:-"${XDG_DATA_HOME:-$HOME/.local/share}/personal-tools/npu/runtime.json"}
if [[ -f "$runtime_json" ]]; then
    audio_library_path=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["libraryPath"])' "$runtime_json")
    export LD_LIBRARY_PATH="$audio_library_path${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
fi
exec "$audio_binary" "$@"
