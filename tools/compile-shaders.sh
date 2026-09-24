#!/usr/bin/env bash
# Rebuilds the GPU tonemap's shader bytecode (src/ToneSnip.Windows/Capture/Shaders/*.cso and Tonemap.hlsl.sha256) from
# Tonemap.hlsl, from WSL. It runs ShaderBytecodeTests with the Windows .NET SDK and TONESNIP_WRITE_SHADERS=1, so the
# bytecode comes from the in-box d3dcompiler_47.dll with the app's own compiler flags: no fxc and no Windows SDK.
# On Windows itself: TONESNIP_WRITE_SHADERS=1 dotnet test tests/ToneSnip.Core.Tests --filter ShaderBytecode
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
cd "$(dirname "$0")/.."
WINDOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
NET_EXE="/mnt/c/Windows/System32/net.exe"
WINUSER=$(cd /mnt/c && /mnt/c/Windows/System32/cmd.exe /c "echo %USERNAME%" 2>/dev/null | tr -d '\r\n')

# dotnet.exe picks its SDK from the global.json in its working directory, and cannot run in a WSL one (as publish.sh).
SDK_DIR=$(mktemp -d "/mnt/c/Users/$WINUSER/AppData/Local/Temp/tonesnip-sdk.XXXXXX")
cp global.json "$SDK_DIR/global.json"
DRIVE=""
for letter in W X Y V U T; do
  if (cd /mnt/c && "$NET_EXE" use "$letter:" "\\\\wsl.localhost\\${WSL_DISTRO_NAME:-}" /persistent:no >/dev/null 2>&1); then DRIVE=$letter; break; fi
done
[ -n "$DRIVE" ] || { echo "no free drive letter to map the WSL share onto" >&2; exit 1; }
# The Linux and Windows SDKs corrupt each other's obj/, so the test project starts and ends clean.
clean() { rm -rf src/ToneSnip.Core/obj src/ToneSnip.Core/bin tests/ToneSnip.Core.Tests/obj tests/ToneSnip.Core.Tests/bin; }
cleanup() { (cd /mnt/c && "$NET_EXE" use "$DRIVE:" /delete >/dev/null 2>&1) || true; rm -rf "$SDK_DIR"; clean; }
trap cleanup EXIT
clean
ROOT="$DRIVE:$(pwd | sed 's|/|\\|g')"
(cd "$SDK_DIR" && TONESNIP_WRITE_SHADERS=1 WSLENV="${WSLENV:+$WSLENV:}TONESNIP_WRITE_SHADERS" \
  "$WINDOTNET" test "$ROOT\\tests\\ToneSnip.Core.Tests\\ToneSnip.Core.Tests.csproj" -c Release --filter ShaderBytecode) | tr -d '\r' | tail -5
ls -l src/ToneSnip.Windows/Capture/Shaders/
