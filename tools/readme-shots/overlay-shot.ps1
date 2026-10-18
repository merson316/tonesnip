param(
    [string]$ExePath = "$env:LOCALAPPDATA\Temp\tonesnip-shots\tonesnip-debug.exe",
    [string]$Out     = "$env:LOCALAPPDATA\Temp\tonesnip-shots\readme-overlay.png",
    [string]$Theme   = "dark"
)
# A picture of the real snip screen for the README: `--hold overlay-picture` puts it over the synthetic HDR picture
# on the primary monitor, a rectangle is dragged on it, and the primary monitor is copied from the screen. The drag
# is sent only while the hold has the foreground, and the hold is ended by closing its process, never by keys.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ShotWin {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
}
"@
[void][ShotWin]::SetProcessDpiAwarenessContext([IntPtr](-4))
function Get-ForegroundPid { $p = [uint32]0; [void][ShotWin]::GetWindowThreadProcessId([ShotWin]::GetForegroundWindow(), [ref]$p); [int]$p }

$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$proc = Start-Process -FilePath $ExePath -ArgumentList @('--hold', 'overlay-picture', '60', '--theme', $Theme) -PassThru
try {
    Start-Sleep -Seconds 4
    if ($proc.HasExited) { throw "the hold exited (code $($proc.ExitCode))" }
    if ((Get-ForegroundPid) -ne $proc.Id) { throw "BLOCKED: the snip screen does not have the foreground (pid $(Get-ForegroundPid)); nothing sent" }
    $x1 = $b.Left + [int]($b.Width * 0.22); $y1 = $b.Top + [int]($b.Height * 0.12)
    $x2 = $b.Left + [int]($b.Width * 0.78); $y2 = $b.Top + [int]($b.Height * 0.70)
    winapp ui drag "$x1,$y1" "$x2,$y2" -a $proc.Id --dwell-ms 400 | Out-Null
    # Rest the pointer inside the selection, so the readout shows nits over the picture.
    Start-Sleep -Milliseconds 1500
    if ((Get-ForegroundPid) -ne $proc.Id) { throw "BLOCKED: lost the foreground after the drag" }
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "saved $Out ($($b.Width)x$($b.Height)); selection $x1,$y1 to $x2,$y2"
} finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
