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
window, `screenshots\`, `overlay-mode-switch.mp4`. Each result is `PASS`, `FAIL` or `BLOCKED`; the exit code is
0 when everything passed, 1 when anything failed and 2 when nothing failed but some input was blocked.

`-Sections settings,editor` runs a subset (`settings`, `editor`, `editor-hdr`, `flyout`, `flyout-grid`,
`toolbar`, `toolbar-annotate`, `countdown`, `textentry`, `toast`, `toast-dwell`, `toast-notice`, `pin`, `gallery`);
`-Theme light` drives the holds in the light theme; `-SkipGallery` drops the stock-control probe, which
is the only section that opens an app other than ToneSnip.

**Input guard.** Raw input goes to the foreground window, whichever app owns it. So before every verb that
injects input (`click`, `drag`, `hover`, `send-keys`, `scroll --wheel`, `touch`, `pen`) and before its own
pointer moves, the script checks that the foreground window belongs to the hold it is driving (`-a <pid>` or
`-w <hwnd>`; a named app such as `-a tonesnip` is refused outright). The bars and the toast card are no-activate windows and never take the foreground by
themselves, so before a pointer verb the guard first gives the element UIA focus, which activates its window. If
the target is still not in the foreground, nothing is sent and the test
is recorded as `BLOCKED` with the foreground pid, counted apart from pass and fail in the summary and in
`test-results.json` (`blocked`). The pointer move that clears a hover is skipped with a note instead, so the section's UIA checks still run. UIA verbs
(`invoke`, `focus`, `set-value`, `wait-for`, `inspect`) act on the element itself and are not guarded. Blocked
tests mean the run is incomplete: keep the desktop idle and rerun those sections.

The script refuses to start while a `tonesnip-overlay` window is up, never sends Ctrl+S at the editor and
never confirms a history row's delete prompt — all three could touch the user's own files.
