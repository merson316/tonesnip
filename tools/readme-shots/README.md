# README screenshots

The pictures in `assets/readme` come from the debug build's harness, never from a real desktop: windows are held over a
plain full-screen backdrop (`backdrop.ps1`), and the snip screen is shown over the synthetic HDR test picture
(`--hold overlay-picture`, driven by `overlay-shot.ps1`). Each picture exists in a dark and a light version, and the
README picks the one matching the reader's theme.

To retake them, from WSL:

```bash
tools/publish.sh debug
S=/mnt/c/Users/<windows-user>/AppData/Local/Temp/tonesnip-shots
cp dist/tonesnip-debug/* tools/ui-tests/ui-tests.ps1 tools/readme-shots/*.ps1 "$S/"
```

then in a Windows terminal that has the foreground (the suite's windows can only take the foreground from it), and
without touching the desktop until it ends:

```powershell
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Temp\tonesnip-shots\take.ps1"
```

and finally `python3 tools/readme-shots/make-images.py "$S"`. Look at every picture before committing it.
