#!/usr/bin/env bash
set -euo pipefail
project_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$project_root"
dotnet_bin=${PERSONALTOOLS_DOTNET:-"$project_root/build/linux/toolchain/dotnet/dotnet"}
if [[ ! -x "$dotnet_bin" ]]; then dotnet_bin=$(command -v dotnet); fi
python3 Scripts/Linux/generate-design.py
cmake -S . -B build/linux/native -G Ninja -DCMAKE_BUILD_TYPE=RelWithDebInfo
cmake --build build/linux/native --parallel 4
ctest --test-dir build/linux/native --output-on-failure
if [[ ! -f build/linux/shell/build.ninja ]]; then meson setup build/linux/shell Sources/Linux/Shell; fi
meson compile -C build/linux/shell
# The service tests own Personal Tools bus names; keep them off the desktop's bus.
dbus-run-session -- "$dotnet_bin" run --project Tests/Linux/Service/PersonalTools.Service.Tests.csproj
"$dotnet_bin" publish Sources/Linux/Service/PersonalTools.Service.csproj -c Release -r linux-x64 --self-contained true -o build/linux/publish/service
printf '%s\n' 'Linux components built and tests completed.'
