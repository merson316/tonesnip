#!/usr/bin/env bash
# Builds gpu-tonemap-bench from WSL with the Windows .NET SDK into dist/gpu-tonemap-bench/.
# The worktree is reached through a mapped drive letter, as tools/publish.sh does.
set -euo pipefail
cd "$(dirname "$0")/../.."
WINDOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
NET_EXE="/mnt/c/Windows/System32/net.exe"
DRIVE=""
for letter in W X Y V U T; do
  if (cd /mnt/c && "$NET_EXE" use "$letter:" "\\\\wsl.localhost\\${WSL_DISTRO_NAME:-Ubuntu}" /persistent:no >/dev/null 2>&1); then DRIVE=$letter; break; fi
done
[ -n "$DRIVE" ] || { echo "no free drive letter" >&2; exit 1; }
trap '(cd /mnt/c && "$NET_EXE" use "$DRIVE:" /delete >/dev/null 2>&1) || true' EXIT
ROOT="$DRIVE:$(pwd | sed 's|/|\\|g')"
rm -rf src/ToneSnip.Core/obj src/ToneSnip.Core/bin src/ToneSnip.Windows/obj src/ToneSnip.Windows/bin tools/gpu-tonemap-bench/obj tools/gpu-tonemap-bench/bin dist/gpu-tonemap-bench
(cd /mnt/c && "$WINDOTNET" publish "$ROOT\\tools\\gpu-tonemap-bench\\GpuTonemapBench.csproj" -c Release -r win-x64 --self-contained false -o "$ROOT\\dist\\gpu-tonemap-bench") | tr -d '\r' | grep -E "error|warning|->" || true
ls -la dist/gpu-tonemap-bench/gpu-tonemap-bench.exe
