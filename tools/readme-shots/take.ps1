param([string]$Dir = "$env:LOCALAPPDATA\Temp\tonesnip-shots")
# Takes the README's screenshots with the debug build in $Dir (tonesnip-debug.exe, ui-tests.ps1 and this folder's
# scripts copied there): for each theme, the UI suite's Settings, HDR editor and Recent grid holds over a plain
# backdrop, then the snip screen over the synthetic picture. Run it from a terminal that has the foreground, and
# leave the desktop alone until it ends; make-images.py then turns the shots into assets/readme.
$ErrorActionPreference = 'Stop'
$ps = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
foreach ($theme in 'dark', 'light') {
    $colour = if ($theme -eq 'dark') { '#1F1F1F' } else { '#E8ECF2' }
    $backdrop = Start-Process -FilePath $ps -WindowStyle Hidden -PassThru -ArgumentList @(
        '-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File', "`"$Dir\backdrop.ps1`"", '-Color', $colour, '-Seconds', '900')
    try {
        Start-Sleep -Seconds 3
        & $ps -NoProfile -ExecutionPolicy Bypass -File "$Dir\ui-tests.ps1" -Sections settings,editor-hdr,flyout-grid -SkipGallery `
            -Theme $theme -OutDir "$Dir\readme-$theme"
    } finally { Stop-Process -Id $backdrop.Id -Force -ErrorAction SilentlyContinue }
    & $ps -NoProfile -ExecutionPolicy Bypass -File "$Dir\overlay-shot.ps1" -Theme $theme -Out "$Dir\readme-overlay-$theme.png"
}
