#!/usr/bin/env bash
# Builds ToneSnip from WSL into dist/ with the Windows .NET SDK:
#   tools/publish.sh [exe]      tonesnip.exe, the production app (single file, .NET framework-dependent, Windows App
#                               SDK self-contained) into dist/tonesnip/, then installs it to %USERPROFILE%\Tools\tonesnip\
#                               and starts it
#   tools/publish.sh debug      tonesnip-debug.exe, the app with the harness compiled in (-p:ToneSnipHarness=true),
#                               into dist/tonesnip-debug/; it has its own mutex, pipes and %LOCALAPPDATA% folder so it
#                               can run beside the installed app. Never installed or started.
#   tools/publish.sh msix       the MSIX package, into dist/msix/
# Environment:
#   TONESNIP_NO_INSTALL=1       build only: no install and no start
#   TONESNIP_PFX=<windows path to .pfx> TONESNIP_PFX_PASSWORD=...   sign the MSIX (unsigned otherwise, which will not install)
#   TONESNIP_NO_ANALYZER=1      skip the Windows App SDK XAML analyzer
#   TONESNIP_ANALYZER=required  fail when the analyzer payload cannot be found (a warning otherwise)
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then export PATH="$HOME/.dotnet:$PATH"; fi
cd "$(dirname "$0")/.."
TARGET="${1:-exe}"
dotnet test tests/ToneSnip.Core.Tests -c Release

WINUSER=""
if [ -x /mnt/c/Windows/System32/cmd.exe ]; then WINUSER=$(cd /mnt/c && /mnt/c/Windows/System32/cmd.exe /c "echo %USERNAME%" 2>/dev/null | tr -d '\r\n'); fi

# ---------------------------------------------------------------------------------------------------------------
# The Windows App SDK analyzer (WUI rules, e.g. a missing AutomationProperties.AutomationId). Directory.Build.props
# loads it only when WinUIAnalyzerDir points at a Windows-visible copy of the payload.
#
# The payload is copied from a WSL path into the Windows profile's temp folder, where the Windows SDK can read it.
# WSLENV must name WinUIAnalyzerDir: a Windows process started from WSL inherits only the variables WSLENV lists.
#
# A missing payload is a warning, not a failure, unless TONESNIP_ANALYZER=required.
ANALYZER_STATE="not supplied"
find_analyzer() {
  if [ -n "${TONESNIP_NO_ANALYZER:-}" ]; then ANALYZER_STATE="off (TONESNIP_NO_ANALYZER=1)"; return 0; fi
  # An explicit Windows-visible WinUIAnalyzerDir takes precedence.
  if [ -n "${WinUIAnalyzerDir:-}" ]; then
    ANALYZER_STATE="on, from WinUIAnalyzerDir=$WinUIAnalyzerDir"
    export WSLENV="${WSLENV:+$WSLENV:}WinUIAnalyzerDir"
    return 0
  fi
  local src="" candidate
  # Known install locations of the analyzer payload (the WinUI tooling's skill folder); the last match wins.
  for candidate in \
      "${CLAUDE_PLUGIN_ROOT:-/nonexistent}/skills/winui-dev-workflow/analyzer" \
      "$HOME"/.claude/plugins/marketplaces/win-dev-skills/plugins/winui/agent-plugin/skills/winui-dev-workflow/analyzer \
      "$HOME"/.claude/plugins/cache/win-dev-skills/winui/*/agent-plugin/skills/winui-dev-workflow/analyzer; do
    [ -f "$candidate/Microsoft.WindowsAppSDK.Analyzers.dll" ] && [ -f "$candidate/Microsoft.WindowsAppSDK.Analyzers.targets" ] && src="$candidate"
  done
  if [ -z "$src" ] || [ -z "$WINUSER" ]; then
    ANALYZER_STATE="NOT SUPPLIED - no WUI rule ran, so this build cannot claim 0 WUI diagnostics"
    if [ "${TONESNIP_ANALYZER:-}" = required ]; then echo "analyzer: $ANALYZER_STATE (TONESNIP_ANALYZER=required)" >&2; return 1; fi
    return 0
  fi
  local dest="/mnt/c/Users/$WINUSER/AppData/Local/Temp/tonesnip-analyzer"
  mkdir -p "$dest"
  cp -f "$src/Microsoft.WindowsAppSDK.Analyzers.dll" "$src/Microsoft.WindowsAppSDK.Analyzers.targets" "$dest/"
  export WinUIAnalyzerDir="C:\\Users\\$WINUSER\\AppData\\Local\\Temp\\tonesnip-analyzer"
  export WSLENV="${WSLENV:+$WSLENV:}WinUIAnalyzerDir"
  ANALYZER_STATE="on, from $src"
  return 0
}

# Every Windows publish is teed into BUILD_LOG so its warnings can be counted afterwards.
BUILD_LOG="$(mktemp -t tonesnip-publish.XXXXXX)"
winpublish() { (cd /mnt/c && "$WINDOTNET" publish "$@") | tr -d '\r' | tee -a "$BUILD_LOG"; }

# Any WUI diagnostic fails the publish: fix it, or silence it in tools/winui-analyzer.globalconfig.
report_analyzer() {
  local wui warn
  wui=$(grep -c -E ' warning WUI[0-9]{4}' "$BUILD_LOG" || true)
  warn=$(grep -c -E ': warning ' "$BUILD_LOG" || true)
  echo "analyzer: $ANALYZER_STATE"
  case "$ANALYZER_STATE" in
    on*)
      echo "analyzer: $wui WUI diagnostics, $warn MSBuild warnings in this publish"
      if [ "$wui" -ne 0 ]; then
        echo "analyzer: FAILED - $wui WUI diagnostic(s); fix them or silence them in tools/winui-analyzer.globalconfig" >&2
        grep -E ' warning WUI[0-9]{4}' "$BUILD_LOG" >&2 || true
        return 1
      fi
      ;;
    *)   echo "analyzer: $warn MSBuild warnings in this publish (WUI rules did not run)" ;;
  esac
  return 0
}

find_analyzer

# The WinUI app builds only with the Windows .NET SDK. It sees this worktree through a mapped drive letter because
# mt.exe (which merges the Windows App SDK activation manifest in a self-contained build) cannot open UNC paths.
WINDOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
NET_EXE="/mnt/c/Windows/System32/net.exe"
WINDRIVE=""
WINDRIVE_PERSIST=""

map_wsl_drive() {
  # /persistent:no keeps the mapping out of the profile, but it also changes net use's default, so the current
  # default is recorded here and restored by unmap_wsl_drive.
  local state letter
  state=$( (cd /mnt/c && "$NET_EXE" use 2>/dev/null) || true )   # read whole, not through grep -q: net.exe dies on SIGPIPE
  case "$state" in *"will not be remembered"*) WINDRIVE_PERSIST=no ;; *) WINDRIVE_PERSIST=yes ;; esac
  for letter in W X Y V U T; do
    if (cd /mnt/c && "$NET_EXE" use "$letter:" "\\\\wsl.localhost\\${WSL_DISTRO_NAME:-}" /persistent:no >/dev/null 2>&1); then
      WINDRIVE="$letter"; return 0
    fi
  done
  echo "no free drive letter to map \\\\wsl.localhost\\${WSL_DISTRO_NAME:-} onto; the WinUI build needs one (WSL only)" >&2
  return 1
}

unmap_wsl_drive() {
  [ -n "$WINDRIVE" ] && { (cd /mnt/c && "$NET_EXE" use "$WINDRIVE:" /delete >/dev/null 2>&1) || true; }
  [ -n "$WINDRIVE_PERSIST" ] && { (cd /mnt/c && "$NET_EXE" use "/persistent:$WINDRIVE_PERSIST" >/dev/null 2>&1) || true; }
  WINDRIVE=""; WINDRIVE_PERSIST=""
  return 0
}

# The only EXIT trap: a second `trap ... EXIT` would replace this one rather than add to it.
cleanup_on_exit() { unmap_wsl_drive; rm -f "$BUILD_LOG"; }
trap cleanup_on_exit EXIT

# Repo-relative path -> Windows path on the mapped drive. The publish runs from /mnt/c because dotnet.exe refuses a
# WSL working directory, so the repo root is captured here.
REPO_ROOT="$(pwd)"
winpath() { echo "$WINDRIVE:$(echo "$REPO_ROOT" | sed 's|/|\\|g')\\$(echo "$1" | sed 's|/|\\|g')"; }

# The Linux SDK (Core tests above) and the Windows SDK corrupt each other's obj/, so every Windows build starts clean.
clean_obj() { rm -rf src/ToneSnip.Core/obj src/ToneSnip.Core/bin src/ToneSnip.Windows/obj src/ToneSnip.Windows/bin src/ToneSnip/obj src/ToneSnip/bin; }

build_exe() {  # the production exe: single file, .NET framework-dependent (needs the .NET 9 Desktop Runtime)
  # WindowsAppSDKSelfContained=true is required: a single-file publish with a framework-dependent Windows App SDK is
  # unsupported, and every XAML window throws XamlParseException. The csproj refuses that combination.
  rm -rf dist/tonesnip
  clean_obj
  map_wsl_drive
  winpublish "$(winpath src/ToneSnip/ToneSnip.csproj)" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:WindowsAppSDKSelfContained=true -o "$(winpath dist/tonesnip)"
  unmap_wsl_drive
  report_analyzer
}

build_debug() {  # the harness build, as tonesnip-debug.exe
  rm -rf dist/tonesnip-debug
  clean_obj
  map_wsl_drive
  # .NET self-contained. -p:ToneSnipHarness=true is a global property, so it also reaches ToneSnip.Core and
  # ToneSnip.Windows, where the pipe names, the data folder and the log level are switched.
  winpublish "$(winpath src/ToneSnip/ToneSnip.csproj)" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:WindowsAppSDKSelfContained=true -p:ToneSnipHarness=true -o "$(winpath dist/tonesnip-debug)"
  unmap_wsl_drive
  report_analyzer
  echo "built: dist/tonesnip-debug/tonesnip-debug.exe ($(stat -c %s dist/tonesnip-debug/tonesnip-debug.exe) bytes)"
}

build_msix() {  # the sideload package: fully self-contained, into dist/msix/
  rm -rf dist/msix
  clean_obj
  local sign=(-p:AppxPackageSigningEnabled=false)
  if [ -n "${TONESNIP_PFX:-}" ] && [ -n "${TONESNIP_PFX_PASSWORD:-}" ]; then
    sign=(-p:AppxPackageSigningEnabled=true -p:PackageCertificateKeyFile="$TONESNIP_PFX" -p:PackageCertificatePassword="${TONESNIP_PFX_PASSWORD:-}")
  else
    echo "TONESNIP_PFX and TONESNIP_PFX_PASSWORD not both set: building the MSIX unsigned (an unsigned package will not install, but it proves the packaging)"
  fi
  map_wsl_drive
  # AppxPackageDir has to be an absolute Windows path with a trailing separator, hence the mapped drive.
  winpublish "$(winpath src/ToneSnip/ToneSnip.csproj)" -c Release -r win-x64 \
      -p:WindowsPackageType=MSIX -p:GenerateAppxPackageOnBuild=true -p:AppxPackageDir="$(winpath dist/msix)\\" \
      "${sign[@]}" -o "$(winpath dist/msix-publish)"
  unmap_wsl_drive
  report_analyzer
  # The appx targets write ToneSnip_<version>_x64.msix into a ..._Test folder; copy it out under the release name.
  local pkg
  pkg=$(find dist/msix -name '*.msix' -print -quit)   # -print -quit, not | head -1: find would die on SIGPIPE under pipefail
  [ -n "$pkg" ] || { echo "no .msix produced under dist/msix" >&2; return 1; }
  cp "$pkg" dist/msix/tonesnip-x64.msix
  echo "built: dist/msix/tonesnip-x64.msix ($(stat -c %s dist/msix/tonesnip-x64.msix) bytes, from $pkg)"
}

install_exe() {  # exe-name: report the size, then install into the Tools folder unless TONESNIP_NO_INSTALL is set
  local name="${1:?install_exe needs a name}" exe
  exe="dist/$name/$name.exe"
  [ -f "$exe" ] || { echo "no $exe to install" >&2; return 1; }
  echo "built: $exe ($(stat -c %s "$exe") bytes)"
  [ -z "${TONESNIP_NO_INSTALL:-}" ] || { echo "TONESNIP_NO_INSTALL=1: not installing $name"; return; }
  [ -n "$WINUSER" ] || { echo "no Windows profile; copy $exe wherever you like"; return; }
  local dir="/mnt/c/Users/$WINUSER/Tools/$name"
  (cd /mnt/c && /mnt/c/Windows/System32/taskkill.exe /IM "$name.exe" /F >/dev/null 2>&1 || true); sleep 1
  # A single-file exe extracts its native payload to %TEMP%\.net\<name>\<hash>\ and nothing deletes it; each rebuild
  # gets a new hash. Safe to remove now that the app is stopped.
  rm -rf "/mnt/c/Users/$WINUSER/AppData/Local/Temp/.net/$name"
  # Replace the directory rather than copy into it: a WinUI exe started beside stale native DLLs can crash.
  # The ${1:?} guard above keeps an empty name from turning this into the whole Tools folder.
  rm -rf "$dir"
  mkdir -p "$dir"
  cp "$exe" "$dir/"
  echo "installed: $dir/$name.exe"
}

# True when the caller wants the freshly installed exe started (a Windows profile, and no TONESNIP_NO_INSTALL).
may_start() {  # exe-name
  [ -z "${TONESNIP_NO_INSTALL:-}" ] || { echo "TONESNIP_NO_INSTALL=1: not starting $1"; return 1; }
  [ -n "$WINUSER" ]
}

case "$TARGET" in
  exe)
    build_exe
    install_exe tonesnip
    if may_start tonesnip; then (cd /mnt/c && /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -Command "Start-Process 'C:\\Users\\$WINUSER\\Tools\\tonesnip\\tonesnip.exe'" >/dev/null 2>&1 && echo "ToneSnip started") || echo "could not start ToneSnip; run it from the Tools folder"; fi ;;
  msix)
    build_msix ;;
  debug)
    # No install and no start: the debug exe is copied by hand to wherever it is driven from (see tools/ui-tests).
    build_debug ;;
  *)
    echo "unknown target '$TARGET'; expected exe, debug or msix" >&2
    exit 1 ;;
esac
