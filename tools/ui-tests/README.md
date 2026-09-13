# `winapp ui` verification pass

`ui-tests.ps1` is the whole-app UI Automation pass: every window in one run, driven through
`tonesnip-debug.exe`'s `--hold <window> <seconds>` mode, with per-window accessibility audits, value
round-trips, accelerators, hover states, the overlay mode-switch recording and a screenshot of every state.

It needs the **debug build**, not the shipping exe: `tonesnip.exe` has no `--hold`, so it would start a second
tray instance instead of opening a window. `tonesnip-debug.exe` keeps its own mutex, pipes and
`%LOCALAPPDATA%\tonesnip-debug` folder, so nothing this pass does reaches the settings, history or thumbnails
of an installed ToneSnip. The script refuses any other exe.

**Run it** (Windows PowerShell 5.1, from a copy of the built exe — never the installed build under
`%USERPROFILE%\Tools\tonesnip`, and never `-a tonesnip`, which would attach to the running instance):

```powershell
tools/publish.sh debug                                    # from WSL; writes dist/tonesnip-debug/
copy dist\tonesnip-debug\* $env:LOCALAPPDATA\Temp\tonesnip-shots\
powershell -NoProfile -ExecutionPolicy Bypass -File tools\ui-tests\ui-tests.ps1
```

From WSL, where PowerShell is not on `PATH`:

```bash
cd /mnt/c && /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile \
  -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Local\Temp\tonesnip-shots\ui-tests.ps1'
```

Results land in `%LOCALAPPDATA%\Temp\tonesnip-shots\ui\`: `test-results.json`, `a11y-<window>.txt` per
window, `screenshots\`, `overlay-mode-switch.mp4`. Exit code is 0 only when nothing failed.

`-Sections settings,editor` runs a subset (`settings`, `editor`, `editor-hdr`, `flyout`, `flyout-grid`,
`toolbar`, `toolbar-annotate`, `countdown`, `textentry`, `toast`, `toast-dwell`, `gallery`);
`-Theme light` drives the holds in the light theme; `-SkipGallery` drops the stock-control probe, which
is the only section that opens an app other than ToneSnip.

The script refuses to start while a `tonesnip-overlay` window is up, never sends Ctrl+S at the editor and
never confirms a history row's delete prompt — all three could touch the user's own files.
