<#
    ToneSnip - the full winapp ui verification pass.

    One run, one results file, every window. Each window is opened through the debug build's
    `--hold <window> <seconds>` mode, driven with `winapp ui`, asserted on, photographed and killed
    before the next one opens; ToneSnip's windows never coexist, so every section launches and targets
    its own pid.

    THE EXE THIS DRIVES is tonesnip-debug.exe (`tools/publish.sh debug`), never tonesnip.exe: the
    shipping build has no `--hold` and would start a second tray instance. tonesnip-debug.exe has its
    own mutex, pipes and %LOCALAPPDATA% folder, so nothing it does reaches the installed app's data.

    RULES THIS SCRIPT ENFORCES
      * It refuses to open anything while a `tonesnip-overlay` window is up (a snip in flight).
      * It runs the copy under %LOCALAPPDATA%\Temp\tonesnip-shots\, never the installed build under
        %USERPROFILE%\Tools\tonesnip, and never `-a tonesnip` (which would attach to the installed instance).
      * It never sends Ctrl+S at the editor and never presses the delete prompt's own Delete on a history row: both
        write to (or remove) the user's own files.
      * Every hover ends with an INJECTED pointer move (mouse_event MOUSEEVENTF_MOVE|ABSOLUTE), not
        SetCursorPos: a teleport raises no PointerExited in a XAML island, so a hover left standing makes
        the next assertion read as broken.

    ASCII only, on purpose: Windows PowerShell 5.1 reads a UTF-8 script as ANSI and mis-parses any
    non-ASCII character in it.

    Usage:
      powershell -NoProfile -ExecutionPolicy Bypass -File ui-tests.ps1
      powershell -NoProfile -ExecutionPolicy Bypass -File ui-tests.ps1 -Sections settings,editor
#>
param(
    [string]   $ExePath  = "$env:LOCALAPPDATA\Temp\tonesnip-shots\tonesnip-debug.exe",
    [string]   $OutDir   = "$env:LOCALAPPDATA\Temp\tonesnip-shots\ui",
    [string]   $Theme    = "dark",
    [string[]] $Sections = @(),
    [switch]   $SkipGallery
)

$ErrorActionPreference = 'Continue'
$env:WINAPP_TELEMETRY_OPTOUT = '1'

# `powershell -File ... -Sections editor,flyout` passes "editor,flyout" as ONE string (-File does not
# split lists for [string[]] parameters), so split it here.
$Sections = @($Sections | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($Sections.Count -gt 0) { Write-Host "  only  $($Sections -join ', ')" }

$script:pass = 0
$script:fail = 0
$script:results = @()
$script:notes = @()

$ShotDir = Join-Path $OutDir 'screenshots'
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null

Add-Type -Namespace TS -Name Win -MemberDefinition @'
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr p, IntPtr c, string cls, string name);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
'@

# ---------------------------------------------------------------- plumbing

function Write-Note { param([string]$Text) $script:notes += $Text; Write-Host "  ..    $Text" -ForegroundColor DarkGray }

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    $global:LASTEXITCODE = 0
    try {
        $output = & $Script 2>&1
        if ($LASTEXITCODE -eq 0) {
            $script:pass++
            $script:results += @{ name = $Name; status = "PASS" }
            Write-Host "  PASS  $Name" -ForegroundColor Green
        } else {
            $script:fail++
            $detail = (($output | Out-String) -replace '\s+', ' ').Trim()
            $script:results += @{ name = $Name; status = "FAIL"; detail = $detail }
            Write-Host "  FAIL  $Name -- $detail" -ForegroundColor Red
        }
    } catch {
        $script:fail++
        $script:results += @{ name = $Name; status = "FAIL"; detail = "$_" }
        Write-Host "  FAIL  $Name -- $_" -ForegroundColor Red
    }
}

# An injected move, not a cursor set. See the header.
function Move-PointerAway {
    param([int]$X = 24, [int]$Y = 24)
    $w = [TS.Win]::GetSystemMetrics(0); $h = [TS.Win]::GetSystemMetrics(1)
    if ($w -le 0 -or $h -le 0) { return }
    [TS.Win]::mouse_event(0x8001, [uint32](($X * 65535) / $w), [uint32](($Y * 65535) / $h), 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
}

function Assert-NoOverlay {
    $h = [TS.Win]::FindWindowExW([IntPtr]::Zero, [IntPtr]::Zero, 'tonesnip-overlay', $null)
    if ($h -ne [IntPtr]::Zero) { throw "OVERLAY GUARD: a tonesnip-overlay window is up (hwnd $h). Refusing to open anything." }
}

function Get-Json {
    param([string[]]$UiArgs)
    $raw = (& winapp ui @UiArgs 2>$null | Out-String)
    # Plain stdout can carry advisory lines above the JSON ("Auto-selected HWND ... from N windows").
    $start = $raw.IndexOfAny([char[]]@('{', '['))
    if ($start -lt 0) { return $null }
    try { return ($raw.Substring($start) | ConvertFrom-Json) } catch { return $null }
}

<#
    `winapp ui inspect --json` answers
        { depth, interactive, windows: [ { hwnd, title, className, elementCount, elements: [ ... ] } ] }
    -- the elements hang off each WINDOW, not off the root. OS dialog hosts are dropped at the window,
    because className is a window property.
#>
function Get-Elements {
    param([int]$TargetPid, [switch]$Interactive, [int]$Depth = 20)
    $a = @('inspect', '-a', "$TargetPid", '--json')
    if ($Interactive) { $a += '--interactive' } else { $a += @('-d', "$Depth") }
    $tree = Get-Json $a
    if (-not $tree) { return @() }
    $out = New-Object System.Collections.ArrayList
    # --interactive answers a flat list per window; the plain tree is nested through `children`. Walked
    # with an explicit stack, since recursion hits PowerShell 5.1's call-depth limit on the editor's tree.
    # Never wrap a possibly-absent property in @(): `@($null)` is a one-element array, so a leaf would
    # push nulls for ever. Test the property first.
    $stack = New-Object System.Collections.Stack
    foreach ($w in @($tree.windows)) {
        if (-not $w) { continue }
        if ($w.className -match 'PickerHost|#32770|CabinetWClass') { continue }
        if ($w.elements) { foreach ($e in $w.elements) { if ($e) { $stack.Push($e) } } }
    }
    $guard = 0
    while ($stack.Count -gt 0 -and $guard -lt 20000) {
        $guard++
        $node = $stack.Pop()
        if (-not $node) { continue }
        [void]$out.Add($node)
        if ($node.children) { foreach ($c in $node.children) { if ($c) { $stack.Push($c) } } }
    }
    if ($guard -ge 20000) { Write-Note "Get-Elements hit its 20000-node guard; the tree walk was truncated" }
    return $out.ToArray()
}

# `get-property --json` answers { elementId, properties: { Name, AutomationId, ControlType, ClassName,
# IsEnabled, IsOffscreen, BoundingRectangle, HasKeyboardFocus, IsKeyboardFocusable, IsPassword, ... } }.
function Get-Prop {
    param([int]$TargetPid, [string]$Selector, [string]$Name)
    $r = Get-Json @('get-property', $Selector, '-a', "$TargetPid", '--json')
    if (-not $r) { return $null }
    if ($Name) { return $r.properties.$Name }
    return $r.properties
}

# The app's own top-level window, so an assertion is not answered by a tooltip or a menu popup that
# happens to be foreground ("Auto-selected HWND ... from 2 windows" is the CLI telling you it guessed).
function Get-MainHwnd {
    param([int]$TargetPid)
    $ws = @(Get-Json @('list-windows', '-a', "$TargetPid", '--json'))
    $w = @($ws | Where-Object { $_.title -ne 'PopupHost' -and $_.width -gt 1 } |
           Sort-Object -Property { [int]$_.width * [int]$_.height } -Descending)[0]
    if ($w) { return [int]$w.hwnd }
    return 0
}

function Start-Hold {
    param([string]$What, [int]$Seconds, [string]$HoldTheme = $Theme)
    Assert-NoOverlay
    $a = @('--hold', $What, "$Seconds")
    if ($HoldTheme -and $HoldTheme -ne 'system') { $a += @('--theme', $HoldTheme) }
    Write-Host "`n=== $What ($Seconds s, theme $HoldTheme)" -ForegroundColor Cyan
    $proc = Start-Process -FilePath $ExePath -ArgumentList $a -PassThru
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        if ($proc.HasExited) { throw "hold '$What' exited (code $($proc.ExitCode)) before a window appeared" }
        $w = Get-Json @('list-windows', '-a', "$($proc.Id)", '--json')
        if ($w) { Start-Sleep -Milliseconds 900; Write-Host "    pid $($proc.Id)" -ForegroundColor DarkGray; return $proc }
    }
    throw "hold '$What' never showed a window"
}

function Stop-Hold {
    param($Proc)
    if ($Proc -and -not $Proc.HasExited) { Stop-Process -Id $Proc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 800
}

# Always --capture-screen: the window-DC path can come back solid black for this app's windows, and a
# screen BitBlt also catches the popups (menus, tooltips, the toast, the bars).
function Shot {
    param([int]$TargetPid, [string]$Name)
    $path = Join-Path $ShotDir "$Name.png"
    & winapp ui screenshot -a $TargetPid --capture-screen -o $path 2>&1 | Out-Null
    if (-not (Test-Path $path)) { Write-Note "screenshot $Name produced no file" }
}

# The window chrome the SDK draws for us: not this app's controls, and not what the audit is about.
$ChromeIds   = @('MinimizeButton','MaximizeButton','RestoreButton','CloseButton','NavigationViewBackButton','TogglePaneButton','SettingsItem','PaneAutoSuggestButton')
$ChromeNames = @('Minimize','Maximize','Restore','Close','System','System menu','Back','Open Navigation','Close Navigation','Settings')

<#
    The per-window accessibility audit:
      (a) every one of the app's own interactive elements carries an automationId;
      (b) every one carries a non-empty name (UIA cannot tell icon-only controls apart, so all are checked);
      (c) no name begins with a private-use codepoint (U+E000..U+F8FF), i.e. a Segoe Fluent glyph
          leaking into an accessible name.

    Coverage limit: only `inspect --interactive` reports automationId, and it omits controls whose peer
    reports a custom ControlType (HotkeyBox comes back as Unknown(50025)). The PASS count is the number
    audited, not the number of controls; the WUI2020 analyzer rule is the exhaustive check for ids.
#>
function Test-A11y {
    param([int]$TargetPid, [string]$Window)
    $els = @(Get-Elements $TargetPid -Interactive)
    if ($els.Count -eq 0) {
        $script:fail++
        $script:results += @{ name = "$Window a11y: tree readable"; status = "FAIL"; detail = "inspect --interactive returned nothing" }
        Write-Host "  FAIL  $Window a11y: inspect returned nothing" -ForegroundColor Red
        return
    }
    $own = @($els | Where-Object {
        ($ChromeIds -notcontains $_.automationId) -and
        -not ((-not $_.automationId) -and ($ChromeNames -contains $_.name))
    })

    # ListView/GridView containers generated per data item: reported on their own line below.
    $rows = @($own | Where-Object { $_.type -match '^(ListItem|DataItem)$' })
    $own  = @($own | Where-Object { $_.type -notmatch '^(ListItem|DataItem)$' })
    if ($rows.Count -gt 0) {
        $rowsNoId = @($rows | Where-Object { -not $_.automationId })
        if ($rowsNoId.Count -eq 0) {
            $script:pass++; $script:results += @{ name = "$Window a11y (a2): every data row has an AutomationId"; status = "PASS"; detail = "$($rows.Count) rows" }
            Write-Host "  PASS  $Window a11y (a2): $($rows.Count) data rows, all with an AutomationId" -ForegroundColor Green
        } else {
            $script:fail++
            $script:results += @{ name = "$Window a11y (a2): every data row has an AutomationId"; status = "FAIL";
                                  detail = "$($rowsNoId.Count) of $($rows.Count) $($rows[0].type) rows carry no AutomationId (they are named: e.g. '$($rowsNoId[0].name)')" }
            Write-Host "  FAIL  $Window a11y (a2): $($rowsNoId.Count) of $($rows.Count) data rows carry no AutomationId (all named)" -ForegroundColor Red
        }
        $rowsNoName = @($rows | Where-Object { -not $_.name -or $_.name.Trim() -eq '' })
        if ($rowsNoName.Count -eq 0) {
            $script:pass++; $script:results += @{ name = "$Window a11y (b2): every data row has a Name"; status = "PASS" }
            Write-Host "  PASS  $Window a11y (b2): every data row named" -ForegroundColor Green
        } else {
            $script:fail++
            $script:results += @{ name = "$Window a11y (b2): every data row has a Name"; status = "FAIL"; detail = "$($rowsNoName.Count) unnamed rows" }
            Write-Host "  FAIL  $Window a11y (b2): $($rowsNoName.Count) unnamed rows" -ForegroundColor Red
        }
    }

    $noId = @($own | Where-Object { -not $_.automationId })
    if ($noId.Count -eq 0) {
        $script:pass++; $script:results += @{ name = "$Window a11y (a): every control has an AutomationId"; status = "PASS"; detail = "$($own.Count) elements" }
        Write-Host "  PASS  $Window a11y (a): $($own.Count) elements, all with an AutomationId" -ForegroundColor Green
    } else {
        $script:fail++
        $d = (($noId | ForEach-Object { "$($_.type) '$($_.name)'" }) -join ', ')
        $script:results += @{ name = "$Window a11y (a): every control has an AutomationId"; status = "FAIL"; detail = $d }
        Write-Host "  FAIL  $Window a11y (a): $d" -ForegroundColor Red
    }

    $noName = @($own | Where-Object { -not $_.name -or $_.name.Trim() -eq '' })
    if ($noName.Count -eq 0) {
        $script:pass++; $script:results += @{ name = "$Window a11y (b): every control has a Name"; status = "PASS" }
        Write-Host "  PASS  $Window a11y (b): every control named" -ForegroundColor Green
    } else {
        $script:fail++
        $d = (($noName | ForEach-Object { "$($_.type) [$($_.automationId)]" }) -join ', ')
        $script:results += @{ name = "$Window a11y (b): every control has a Name"; status = "FAIL"; detail = $d }
        Write-Host "  FAIL  $Window a11y (b): $d" -ForegroundColor Red
    }

    $pua = @($own | Where-Object {
        $_.name -and $_.name.Length -gt 0 -and
        ([int][char]$_.name[0] -ge 0xE000) -and ([int][char]$_.name[0] -le 0xF8FF)
    })
    if ($pua.Count -eq 0) {
        $script:pass++; $script:results += @{ name = "$Window a11y (c): no Name starts with a private-use glyph"; status = "PASS" }
        Write-Host "  PASS  $Window a11y (c): no glyph names" -ForegroundColor Green
    } else {
        $script:fail++
        $d = (($pua | ForEach-Object { "{0} U+{1:X4}" -f $_.automationId, [int][char]$_.name[0] }) -join ', ')
        $script:results += @{ name = "$Window a11y (c): no Name starts with a private-use glyph"; status = "FAIL"; detail = $d }
        Write-Host "  FAIL  $Window a11y (c): $d" -ForegroundColor Red
    }

    ($els | ForEach-Object { "{0,-28} {1,-14} {2}" -f $_.automationId, $_.type, $_.name }) |
        Out-File (Join-Path $OutDir "a11y-$Window.txt") -Encoding ascii
}

<#
    A ComboBox round-trip through the keyboard: `set-value` drives UIA's ValuePattern, which a WinUI 3
    ComboBox does not implement. Down then Up on a closed ComboBox moves the selection and back.
#>
function Test-ComboRoundTrip {
    param([int]$TargetPid, [string]$Selector, [string]$Label)
    $was = (Get-Json @('get-value', $Selector, '-a', "$TargetPid", '--json')).text
    Write-Note "$Selector was '$was'"
    Test-UI "settings: $Label round-trip from '$was'" {
        if (-not $was) { throw "no value to start from" }
        winapp ui focus $Selector -a $TargetPid | Out-Null
        Start-Sleep -Milliseconds 400
        winapp ui send-keys 'down' -a $TargetPid --via send-input | Out-Null
        Start-Sleep -Milliseconds 800
        $now = (Get-Json @('get-value', $Selector, '-a', "$TargetPid", '--json')).text
        if ($now -eq $was) { throw "Down did not move the selection off '$was'" }
        winapp ui send-keys 'up' -a $TargetPid --via send-input | Out-Null
        Start-Sleep -Milliseconds 800
        $back = (Get-Json @('get-value', $Selector, '-a', "$TargetPid", '--json')).text
        Write-Host "        $was -> $now -> $back" -ForegroundColor DarkGray
        if ($back -ne $was) { throw "Up did not return to '$was' (now '$back')" }
    }
}

# An AutomationId set on a container that has no automation peer of its own never reaches UIA. Not a
# failure (every leaf inside carries its own id and name), but worth having on the record.
function Note-DeadId {
    param([int]$TargetPid, [string]$Selector, [string]$Where)
    winapp ui wait-for $Selector -a $TargetPid -t 1500 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Note "$Selector ($Where) is not in the UIA tree: the id sits on a container with no peer" }
    else { Write-Note "$Selector ($Where) is in the UIA tree" }
}

<#
    The Recent flyout closes itself when it loses the foreground, and its `--hold` ends with it. Every
    flyout step goes through this, which reopens the hold when the previous one has dismissed.
#>
$script:flyProc = $null
function Ensure-Flyout {
    param([string]$What, [int]$Seconds = 140, [string]$Probe = 'Flyout_OpenFolder')
    if ($script:flyProc -and -not $script:flyProc.HasExited) {
        winapp ui wait-for $Probe -a $script:flyProc.Id -t 2500 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { return $script:flyProc.Id }
        Write-Note "$What is no longer on screen; reopening (the hold's countdown outlives its window)"
        Stop-Hold $script:flyProc
    }
    $script:flyProc = Start-Hold $What $Seconds
    return $script:flyProc.Id
}

<#
    History row containers are stamped Flyout_Row1, Flyout_Row2, ... from their index (by
    OnContainerContentChanging, and again by StampRowIds once settled), so a client can address row N.
#>
function Test-RowIds {
    param([int]$TargetPid, [string]$Layout)
    $els = @(Get-Elements $TargetPid -Interactive)
    $rows = @($els | Where-Object { $_.type -match '^(ListItem|DataItem)$' })
    Test-UI "${Layout}: every history row carries a Flyout_RowN id" {
        if ($rows.Count -eq 0) { throw "no rows in the tree (is the flyout still on screen, and does the history have entries?)" }
        $bad = @($rows | Where-Object { $_.automationId -notmatch '^Flyout_Row[0-9]+$' })
        if ($bad.Count -gt 0) { throw "$($bad.Count) of $($rows.Count) rows are not Flyout_RowN (first: '$($bad[0].automationId)' / '$($bad[0].name)')" }
        Write-Host "        $($rows.Count) rows, $($rows[0].automationId)..$($rows[-1].automationId)" -ForegroundColor DarkGray
    }
    Test-UI "${Layout}: the row ids are unique (scroll recycling)" {
        $ids = @($rows | ForEach-Object { $_.automationId })
        $dupes = @($ids | Group-Object | Where-Object { $_.Count -gt 1 })
        if ($dupes.Count -gt 0) { throw "duplicate ids: $(($dupes | ForEach-Object { $_.Name }) -join ', ')" }
    }
    Test-UI "${Layout}: the ids are 1-based and in list order" {
        $ordered = @($rows | Sort-Object { [int]($_.automationId -replace '[^0-9]','') })
        for ($i = 0; $i -lt $ordered.Count; $i++) {
            $want = "Flyout_Row$($i + 1)"
            if ($ordered[$i].automationId -ne $want) { throw "position $($i + 1) is '$($ordered[$i].automationId)', expected $want" }
        }
    }
    # Flyout_Row2 must resolve to the SECOND row and only that one -- the prefix it shares with
    # Flyout_RowCopy/RowOpen/RowEdit/RowFolder/RowDelete must not make a selector ambiguous.
    Test-UI "${Layout}: Flyout_Row2 resolves to the second row" {
        if ($rows.Count -lt 2) { throw "only $($rows.Count) row(s) in the history; need 2" }
        $second = @($rows | Where-Object { $_.automationId -eq 'Flyout_Row2' })
        if ($second.Count -ne 1) { throw "$($second.Count) elements answer to Flyout_Row2" }
        $byIndex = @($rows | Sort-Object { [int]($_.automationId -replace '[^0-9]','') })[1]
        if ($second[0].name -ne $byIndex.name) { throw "Flyout_Row2 is '$($second[0].name)', the second row is '$($byIndex.name)'" }
        winapp ui wait-for 'Flyout_Row2' -a $TargetPid -t 3000
        if ($LASTEXITCODE -ne 0) { throw "wait-for could not resolve Flyout_Row2" }
        Write-Host "        Flyout_Row2 = '$($second[0].name)'" -ForegroundColor DarkGray
    }
    Test-UI "${Layout}: the per-row action ids still resolve" {
        foreach ($a in 'Flyout_RowOpen','Flyout_RowCopy','Flyout_RowEdit','Flyout_RowFolder','Flyout_RowDelete') {
            winapp ui wait-for $a -a $TargetPid -t 2500 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "$a no longer resolves" }
        }
    }
}

function Should-Run { param([string]$Name) return ($Sections.Count -eq 0 -or $Sections -contains $Name) }

# ---------------------------------------------------------------- preflight

Write-Host "ToneSnip UI verification pass" -ForegroundColor White
Write-Host "  exe   $ExePath"
Write-Host "  out   $OutDir"

if (-not (Test-Path $ExePath)) { throw "no harness exe at $ExePath (build one with tools/publish.sh debug)" }
if ($ExePath -match '\\Tools\\tonesnip\\')     { throw "refusing to drive the installed build at $ExePath" }
# tonesnip.exe has no --hold: every Start-Hold below would start a tray instance on the user's desktop instead.
if ($ExePath -notmatch 'tonesnip-debug\.exe$') { throw "this pass needs tonesnip-debug.exe (tools/publish.sh debug); got $ExePath" }
Assert-NoOverlay

$running = @(Get-Process tonesnip, tonesnip-debug -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Note "$($running.Count) ToneSnip process(es) already running: $(($running | ForEach-Object { "$($_.ProcessName)/$($_.Id)" }) -join ', '). Every section targets its own pid, never -a tonesnip."
}

# ---------------------------------------------------------------- 1. settings

if (Should-Run 'settings') {
    $p = $null
    try {
        $p = Start-Hold 'settings-general' 260
        $id = $p.Id

        foreach ($nav in 'Settings_NavGeneral','Settings_NavHotkeys','Settings_NavTonemap','Settings_NavOutput','Settings_NavAbout') {
            Test-UI "settings: $nav exists" { winapp ui wait-for $nav -a $id -t 4000 }
        }

        # --- navigation between all five pages, each proved by a control only that page has
        $pages = @(
            @{ nav = 'Settings_NavGeneral'; probe = 'Settings_StartWithWindows'; shot = 'settings-general' },
            @{ nav = 'Settings_NavHotkeys'; probe = 'Settings_HkRegion';         shot = 'settings-hotkeys' },
            @{ nav = 'Settings_NavTonemap'; probe = 'Settings_Tonemap';          shot = 'settings-tonemap' },
            @{ nav = 'Settings_NavOutput';  probe = 'Settings_Format';           shot = 'settings-output'  },
            @{ nav = 'Settings_NavAbout';   probe = 'Settings_GitHub';           shot = 'settings-about'   }
        )
        foreach ($pg in $pages) {
            $nav = $pg.nav; $probe = $pg.probe
            Test-UI "settings: navigate to $nav" { winapp ui invoke $nav -a $id }
            Start-Sleep -Milliseconds 600
            Test-UI "settings: $probe on the page $nav opens" { winapp ui wait-for $probe -a $id -t 5000 }
            Shot $id $pg.shot
        }

        # --- value round-trips. Nothing here reaches settings.json: a hold runs in App.ScreenshotMode,
        #     where ApplySettings is an in-memory update.
        Test-UI "settings: back to General" { winapp ui invoke 'Settings_NavGeneral' -a $id }
        Start-Sleep -Milliseconds 600

        Test-ComboRoundTrip $id 'Settings_ThemeCombo'   'ThemeCombo'
        Test-ComboRoundTrip $id 'Settings_TrayIconCombo' 'TrayIconCombo'

        # StartWithWindows drives Autostart.Apply in the running app. A hold never subscribes SettingsChanged,
        # so this is in-memory; the Run key is read either side to prove it.
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        $runBefore = (Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue).ToneSnip
        $swWas = (Get-Json @('get-value','Settings_StartWithWindows','-a',"$id",'--json')).text
        Write-Note "Settings_StartWithWindows was '$swWas'; HKCU Run\ToneSnip = '$runBefore'"
        Test-UI "settings: StartWithWindows toggles and returns" {
            winapp ui invoke 'Settings_StartWithWindows' -a $id
            if ($LASTEXITCODE -ne 0) { throw "invoke failed" }
            Start-Sleep -Milliseconds 400
            $now = (Get-Json @('get-value','Settings_StartWithWindows','-a',"$id",'--json')).text
            if ($now -eq $swWas) { throw "did not change from $swWas" }
            winapp ui invoke 'Settings_StartWithWindows' -a $id
            Start-Sleep -Milliseconds 400
            winapp ui wait-for 'Settings_StartWithWindows' -a $id --value $swWas -t 4000
        }
        Test-UI "settings: the hold wrote nothing to HKCU Run" {
            $after = (Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue).ToneSnip
            if ($after -ne $runBefore) { throw "HKCU Run\ToneSnip moved: '$runBefore' -> '$after'" }
        }

        Test-UI "settings: NotificationStyle reads a value (Output page)" {
            winapp ui invoke 'Settings_NavOutput' -a $id
            Start-Sleep -Milliseconds 600
            winapp ui wait-for 'Settings_NotificationStyle' -a $id -t 4000
        }
        Test-ComboRoundTrip $id 'Settings_NotificationStyle' 'NotificationStyle'

        # --- the layout picker: value round-trip, then the single-tab-stop proof
        Test-UI "settings: back to General for the layout picker" {
            winapp ui invoke 'Settings_NavGeneral' -a $id
            Start-Sleep -Milliseconds 600
            winapp ui wait-for 'Settings_LayoutRow' -a $id -t 4000
        }
        Test-UI "settings: LayoutRow / LayoutGrid round-trip" {
            winapp ui invoke 'Settings_LayoutGrid' -a $id
            if ($LASTEXITCODE -ne 0) { throw "could not select Grid" }
            Start-Sleep -Milliseconds 400
            winapp ui wait-for 'Settings_LayoutGrid' -a $id -p IsSelected --value 'True' -t 3000
            if ($LASTEXITCODE -ne 0) { winapp ui wait-for 'Settings_LayoutGrid' -a $id -p ToggleState --value 'On' -t 3000 }
            if ($LASTEXITCODE -ne 0) { throw "Grid did not read back as selected" }
            winapp ui invoke 'Settings_LayoutRow' -a $id
            Start-Sleep -Milliseconds 400
        }
        # One tab stop: focus the selected radio, press Tab, and the focus must have LEFT the picker.
        Test-UI "settings: the layout picker is one tab stop" {
            winapp ui focus 'Settings_LayoutRow' -a $id
            if ($LASTEXITCODE -ne 0) { throw "could not focus Settings_LayoutRow" }
            Start-Sleep -Milliseconds 300
            winapp ui send-keys 'tab' -a $id --via send-input | Out-Null
            Start-Sleep -Milliseconds 600
            $f = (& winapp ui get-focused -a $id --json 2>$null | Out-String)
            Write-Host "        after tab, get-focused says: $(($f -replace '\s+',' ').Trim())" -ForegroundColor DarkGray
            if ($f -match 'Settings_LayoutGrid') { throw "Tab moved inside the picker (landed on Settings_LayoutGrid): it is two tab stops, not one" }
        }

        # --- the selection frame picker: each frame reads back as selected, ending on Normal (the default)
        Test-UI "settings: FrameViewfinder / FrameGuides / FrameNormal round-trip" {
            foreach ($frame in 'Settings_FrameViewfinder', 'Settings_FrameGuides', 'Settings_FrameNormal') {
                winapp ui invoke $frame -a $id
                if ($LASTEXITCODE -ne 0) { throw "could not select $frame" }
                Start-Sleep -Milliseconds 400
                winapp ui wait-for $frame -a $id -p IsSelected --value 'True' -t 3000
                if ($LASTEXITCODE -ne 0) { winapp ui wait-for $frame -a $id -p ToggleState --value 'On' -t 3000 }
                if ($LASTEXITCODE -ne 0) { throw "$frame did not read back as selected" }
            }
        }
        Note-DeadId $id 'Settings_LayoutPicker' 'settings'

        # --- hover a card, then get the pointer out of the window
        Test-UI "settings: hover a SettingsCard" { winapp ui hover 'Settings_StartWithWindows' -a $id --dwell-time 1200 }
        Shot $id 'settings-general-hover'
        Move-PointerAway

        # --- the HotkeyBox: its custom peer is the app's other Edit-shaped control, recorded for the
        #     IsPassword probe.
        Test-UI "settings: HotkeyBox reachable on the Hotkeys page" {
            winapp ui invoke 'Settings_NavHotkeys' -a $id
            Start-Sleep -Milliseconds 600
            winapp ui wait-for 'Settings_HkRegion' -a $id -t 4000
        }
        $hk = Get-Prop $id 'Settings_HkRegion'
        Write-Note ("IsPassword probe: Settings_HkRegion type={0} class={1} IsPassword={2}" -f $hk.ControlType, $hk.ClassName, $hk.IsPassword)
        $hk | ConvertTo-Json -Depth 6 | Out-File (Join-Path $OutDir 'probe-hotkeybox.json') -Encoding ascii

        Test-A11y $id 'settings'
    } catch {
        $script:fail++; $script:results += @{ name = "settings section"; status = "FAIL"; detail = "$_" }
        Write-Host "  FAIL  settings section -- $_" -ForegroundColor Red
    } finally { Stop-Hold $p }
}

# ---------------------------------------------------------------- 2. editor (SDR, the newest history entry)

function Get-EditorText {
    param([int]$Target, [string]$Pattern)
    return @(Get-Elements $Target | Where-Object { $_.name -match $Pattern })
}

if (Should-Run 'editor') {
    $p = $null
    try {
        $p = Start-Hold 'editor' 200
        $id = $p.Id

        foreach ($el in 'Editor_CopyButton','Editor_AnnotateToggle','Editor_ZoomInButton','Editor_ZoomOutButton','Editor_FitButton','Editor_MoreButton','Editor_Canvas') {
            Test-UI "editor: $el exists" { winapp ui wait-for $el -a $id -t 4000 }
        }

        # The status strip. TextBlocks carry no AutomationId (they are not interactive), so the assertion
        # goes through the tree: the zoom readout must be a percentage and the dimensions a WxH.
        Test-UI "editor: ctrl+0 fits, and the zoom readout is a percentage" {
            winapp ui send-keys 'ctrl+0' -a $id --via send-input | Out-Null
            Start-Sleep -Milliseconds 800
            $m = Get-EditorText $id '^\d+\s*%$'
            if ($m.Count -eq 0) { throw "no zoom readout matching '<n> %' in the tree" }
            Write-Host "        zoom readout '$($m[0].name)'" -ForegroundColor DarkGray
        }
        Test-UI "editor: the status strip carries the pixel dimensions" {
            $m = Get-EditorText $id '\d+\s*(x|\u00D7)\s*\d+'
            if ($m.Count -eq 0) { throw "no WxH in the status strip" }
            Write-Host "        dimensions '$($m[0].name)'" -ForegroundColor DarkGray
        }
        Test-UI "editor: zoom in changes the readout" {
            $before = (Get-EditorText $id '^\d+\s*%$')[0].name
            winapp ui invoke 'Editor_ZoomInButton' -a $id | Out-Null
            Start-Sleep -Milliseconds 700
            $after = (Get-EditorText $id '^\d+\s*%$')[0].name
            if ($after -eq $before) { throw "zoom readout stayed at $before" }
            Write-Host "        $before -> $after" -ForegroundColor DarkGray
            winapp ui send-keys 'ctrl+0' -a $id --via send-input | Out-Null
        }

        Test-UI "editor: ctrl+c copies (the clipboard holds an image)" {
            winapp ui send-keys 'ctrl+c' -a $id --via send-input | Out-Null
            Start-Sleep -Milliseconds 1500
            Add-Type -AssemblyName System.Windows.Forms
            $ok = $false
            for ($i = 0; $i -lt 6 -and -not $ok; $i++) {
                try { $ok = [System.Windows.Forms.Clipboard]::ContainsImage() } catch { }
                if (-not $ok) { Start-Sleep -Milliseconds 500 }
            }
            if (-not $ok) { throw "the clipboard holds no image after Ctrl+C" }
        }

        Test-UI "editor: the 'a' accelerator toggles annotate on" {
            winapp ui send-keys 'a' -a $id --via send-input | Out-Null
            Start-Sleep -Milliseconds 800
            winapp ui wait-for 'Editor_AnnotateToggle' -a $id -p ToggleState --value 'On' -t 3000
        }
        Test-UI "editor: annotate on brings up the tool row" { winapp ui wait-for 'Tools_Pen' -a $id -t 4000 }
        Shot $id 'editor-annotate'
        Test-UI "editor: the 'a' accelerator toggles annotate off" {
            winapp ui send-keys 'a' -a $id --via send-input | Out-Null
            Start-Sleep -Milliseconds 800
            winapp ui wait-for 'Editor_AnnotateToggle' -a $id -p ToggleState --value 'Off' -t 3000
        }

        Test-UI "editor: hover the More button" { winapp ui hover 'Editor_MoreButton' -a $id --dwell-time 1200 }
        Move-PointerAway
        Shot $id 'editor'
        Test-A11y $id 'editor'

        # LAST in the section, because its final step closes the window on purpose.
        Test-UI "editor: the overflow menu opens" {
            winapp ui invoke 'Editor_MoreButton' -a $id
            Start-Sleep -Milliseconds 800
            winapp ui wait-for 'Editor_OpenFolder' -a $id -t 4000
        }
        Shot $id 'editor-menu'
        # Escape with the menu open must close the menu only. The menu is a windowed popup that does not take
        # activation, so the key lands on the editor, whose own Escape handling closes the window.
        Test-UI "editor: Escape with the menu open closes the MENU" {
            winapp ui send-keys 'esc' -a $id --via send-input | Out-Null
            Start-Sleep -Milliseconds 1000
            winapp ui wait-for 'Editor_OpenFolder' -a $id --gone -t 4000
            if ($LASTEXITCODE -ne 0) { throw "the menu is still up" }
            winapp ui wait-for 'Editor_MoreButton' -a $id -t 4000
            if ($LASTEXITCODE -ne 0) { throw "the window closed with the menu" }
        }
        Test-UI "editor: a second Escape then closes the window" {
            winapp ui send-keys 'esc' -a $id --via send-input | Out-Null
            Start-Sleep -Milliseconds 1500
            winapp ui wait-for 'Editor_MoreButton' -a $id --gone -t 5000
        }
    } catch {
        $script:fail++; $script:results += @{ name = "editor section"; status = "FAIL"; detail = "$_" }
        Write-Host "  FAIL  editor section -- $_" -ForegroundColor Red
    } finally { Stop-Hold $p }
}

# ---------------------------------------------------------------- 3. editor-hdr: zebra, exposure, and the close dialog

if (Should-Run 'editor-hdr') {
    $p = $null
    try {
        # The synthetic HDR snip: no SavedPath, so nothing on disk can be touched by anything below.
        # It cycles four status notes (5 s each) before its own countdown starts -- wait them out.
        $p = Start-Hold 'editor-hdr' 200
        $id = $p.Id
        Start-Sleep -Seconds 24

        Test-UI "editor-hdr: the exposure slider is up" { winapp ui wait-for 'Editor_ExposureSlider' -a $id -t 5000 }
        Test-UI "editor-hdr: the zebra toggle is up"    { winapp ui wait-for 'Editor_ZebraToggle'    -a $id -t 5000 }
        Test-UI "editor-hdr: hover Zebra raises a tooltip" {
            winapp ui hover 'Editor_ZebraToggle' -a $id --dwell-time 1500
            if ($LASTEXITCODE -ne 0) { throw "hover failed" }
            Start-Sleep -Milliseconds 400
        }
        Shot $id 'editor-hdr-zebrahover'
        Move-PointerAway
        Test-UI "editor-hdr: the zebra toggle turns on" {
            winapp ui invoke 'Editor_ZebraToggle' -a $id
            Start-Sleep -Milliseconds 700
            winapp ui wait-for 'Editor_ZebraToggle' -a $id -p ToggleState --value 'On' -t 3000
        }
        Shot $id 'editor-hdr'

        # --- the close-confirmation ContentDialog. Moving the exposure slider counts as an edit, and this
        #     snip has no file to save.
        # -w the main window: a lingering tooltip popup can make `-a` auto-select the wrong HWND.
        Move-PointerAway
        Start-Sleep -Milliseconds 600
        $hwnd = Get-MainHwnd $id
        Write-Note "editor-hdr main hwnd $hwnd"
        Test-UI "editor-hdr: the exposure slider takes a value (an edit)" {
            winapp ui set-value 'Editor_ExposureSlider' '1.5' -w $hwnd
            if ($LASTEXITCODE -ne 0) { throw "set-value on the slider failed" }
            Start-Sleep -Milliseconds 1000
            $ev = @(Get-EditorText $id 'EV')
            $shown = ($ev | ForEach-Object { $_.name }) -join ' | '
            Write-Host "        EV readout: $shown" -ForegroundColor DarkGray
            if ($shown -notmatch '1\.50') { throw "the EV readout says '$shown', not +1.50 EV" }
        }
        Test-UI "editor-hdr: the status strip shows the snip as modified" {
            $m = Get-EditorText $id 'Modified'
            if ($m.Count -eq 0) { throw "no 'Modified' in the tree after the edit" }
        }
        # Alt+F4 is sent to this pid's window; the prompt hangs off AppWindow.Closing.
        Test-UI "editor-hdr: closing an edited snip raises the save-changes dialog" {
            winapp ui send-keys 'alt+f4' -a $id --via send-input --allow-system-keys | Out-Null
            Start-Sleep -Milliseconds 1800
            winapp ui wait-for 'Discard' -a $id -t 5000
        }
        Shot $id 'editor-close-dialog'
        Test-UI "editor-hdr: the dialog offers Save, Discard and Cancel" {
            $names = @(Get-Elements $id -Interactive | Where-Object { $_.type -match 'Button' } | ForEach-Object { $_.name })
            Write-Host "        buttons: $($names -join ', ')" -ForegroundColor DarkGray
            foreach ($want in 'Save','Discard','Cancel') {
                if ($names -notcontains $want) { throw "no '$want' button; saw: $($names -join ', ')" }
            }
        }
        # Cancel, never Save: Save would write a file.
        Test-UI "editor-hdr: Cancel dismisses the dialog and keeps the window" {
            winapp ui invoke 'Cancel' -a $id
            if ($LASTEXITCODE -ne 0) { winapp ui send-keys 'esc' -a $id --via send-input | Out-Null }
            Start-Sleep -Milliseconds 1200
            winapp ui wait-for 'Editor_ExposureSlider' -a $id -t 4000
        }
        Test-A11y $id 'editor-hdr'
    } catch {
        $script:fail++; $script:results += @{ name = "editor-hdr section"; status = "FAIL"; detail = "$_" }
        Write-Host "  FAIL  editor-hdr section -- $_" -ForegroundColor Red
    } finally { Stop-Hold $p }
}

# ---------------------------------------------------------------- 4. flyout, row layout

if (Should-Run 'flyout') {
    try {
        $id = Ensure-Flyout 'flyout-row' 200

        foreach ($el in 'Flyout_List','Flyout_DelayPill','Flyout_SettingsButton','Flyout_OpenFolder') {
            Test-UI "flyout-row: $el exists" { winapp ui wait-for $el -a $id -t 4000 }
        }
        Test-UI "flyout-row: Flyout_Modes is a named Group in the tree" {
            $pr = Get-Prop $id 'Flyout_Modes'
            if (-not $pr) { throw "Flyout_Modes is not in the UIA tree" }
            Write-Host "        Flyout_Modes type=$($pr.ControlType) name='$($pr.Name)'" -ForegroundColor DarkGray
            if ($pr.ControlType -notmatch 'Group') { throw "control type is $($pr.ControlType), not Group" }
            if (-not $pr.Name) { throw "the group has no Name" }
        }
        Shot $id 'flyout-row'

        Test-RowIds $id 'flyout-row'

        $id = Ensure-Flyout 'flyout-row' 200
        Test-UI "flyout-row: the delay pill opens its band" {
            winapp ui invoke 'Flyout_DelayPill' -a $id
            if ($LASTEXITCODE -ne 0) { throw "the pill could not be invoked" }
            Start-Sleep -Milliseconds 1200
            if ($script:flyProc.HasExited) { throw "the flyout dismissed itself on the pill click" }
            winapp ui wait-for 'Flyout_Delay3' -a $id -t 4000
        }
        Test-UI "flyout-row: Flyout_DelayBand is a named Group in the tree" {
            $pr = Get-Prop $id 'Flyout_DelayBand'
            if (-not $pr) { throw "Flyout_DelayBand is not in the UIA tree" }
            Write-Host "        Flyout_DelayBand type=$($pr.ControlType) name='$($pr.Name)'" -ForegroundColor DarkGray
            if ($pr.ControlType -notmatch 'Group') { throw "control type is $($pr.ControlType), not Group" }
            if (-not $pr.Name) { throw "the group has no Name" }
        }
        # The chips inside the group are present and named.
        Test-UI "flyout-row: the delay chips keep their names and set membership" {
            foreach ($chip in 'Flyout_Delay0','Flyout_Delay3','Flyout_Delay5','Flyout_Delay10') {
                $pr = Get-Prop $id $chip
                if (-not $pr) { throw "$chip is gone" }
                if (-not $pr.Name) { throw "$chip has no Name" }
            }
        }
        Shot $id 'flyout-row-delayband'
        winapp ui invoke 'Flyout_DelayPill' -a $id 2>&1 | Out-Null
        Start-Sleep -Milliseconds 600

        # The pill keeps a tooltip up after the invoke, and the shot above brings the process's windows to the
        # foreground in turn; the flyout loses the foreground and dismisses itself, as it would for a real
        # click elsewhere. Reopen it rather than hover a window that has gone.
        $id = Ensure-Flyout 'flyout-row' 200
        Test-UI "flyout-row: hover a row reveals its actions" {
            winapp ui hover 'Flyout_RowOpen' -a $id --dwell-time 1200
            if ($LASTEXITCODE -ne 0) { throw "hover failed" }
            winapp ui wait-for 'Flyout_RowDelete' -a $id -t 3000
        }
        Shot $id 'flyout-row-hover'

        $id = Ensure-Flyout 'flyout-row' 200
        Test-UI "flyout-row: the delay pill hovers" {
            winapp ui hover 'Flyout_DelayPill' -a $id --dwell-time 1500
        }
        Shot $id 'flyout-row-delaypill-hover'

        $id = Ensure-Flyout 'flyout-row' 200
        Test-UI "flyout-row: a row below the fold scrolls into view" {
            winapp ui scroll-into-view 'Flyout_RowEdit' -a $id
        }

        $id = Ensure-Flyout 'flyout-row' 200
        Test-UI "flyout-row: wheel-scrolling leaves the list live" {
            winapp ui hover 'Flyout_RowOpen' -a $id --dwell-time 900 2>&1 | Out-Null
            winapp ui scroll 'Flyout_List' -a $id --wheel -3 2>&1 | Out-Null
            Start-Sleep -Milliseconds 900
            # UIA cannot read the reveal (it is opacity), so this is a shot for the eye plus a liveness check.
            winapp ui wait-for 'Flyout_List' -a $id -t 4000
        }
        Test-RowIds $id 'flyout-row after a scroll'
        Shot $id 'flyout-row-afterscroll'

        $id = Ensure-Flyout 'flyout-row' 200
        Test-A11y $id 'flyout-row'

        # The delete is PROMPTED ONLY, last because the prompt changes the row. Confirming would delete a real
        # file, so Cancel is pressed instead and must put the row back.
        $id = Ensure-Flyout 'flyout-row' 200
        Test-UI "flyout-row: the bin asks before it deletes" {
            winapp ui hover 'Flyout_RowOpen' -a $id --dwell-time 1200 2>&1 | Out-Null
            $rows0 = @(Get-Elements $id -Interactive | Where-Object { $_.type -match '^(ListItem|DataItem)$' }).Count
            winapp ui invoke 'Flyout_RowDelete' -a $id
            if ($LASTEXITCODE -ne 0) { throw "the delete action could not be invoked" }
            Start-Sleep -Milliseconds 1000
            $all = @(Get-Elements $id -Interactive)
            $rows1 = @($all | Where-Object { $_.type -match '^(ListItem|DataItem)$' }).Count
            $confirm = @($all | Where-Object { $_.automationId -eq 'Flyout_RowConfirmDelete' }).Count
            $cancel = @($all | Where-Object { $_.automationId -eq 'Flyout_RowCancelDelete' }).Count
            Write-Host "        rows $rows0 -> $rows1, prompt: $confirm confirm / $cancel cancel" -ForegroundColor DarkGray
            # Zero rows is the flyout having gone, not a row having been removed -- every row goes with the window.
            if ($rows1 -eq 0) { throw "the flyout closed during the press (nothing was deleted)" }
            if ($rows1 -lt $rows0) { throw "a row disappeared on the bin press: $rows0 -> $rows1 rows" }
            if ($confirm -ne 1 -or $cancel -ne 1) { throw "expected one Delete and one Cancel in the prompt, found $confirm and $cancel" }
        }
        Shot $id 'flyout-row-delete-prompt'
        Test-UI "flyout-row: Cancel puts the row back" {
            winapp ui invoke 'Flyout_RowCancelDelete' -a $id
            if ($LASTEXITCODE -ne 0) { throw "Cancel could not be invoked" }
            Start-Sleep -Milliseconds 700
            $left = @(Get-Elements $id -Interactive | Where-Object { $_.automationId -eq 'Flyout_RowConfirmDelete' }).Count
            if ($left -ne 0) { throw "the prompt is still up after Cancel" }
        }
    } catch {
        $script:fail++; $script:results += @{ name = "flyout-row section"; status = "FAIL"; detail = "$_" }
        Write-Host "  FAIL  flyout-row section -- $_" -ForegroundColor Red
    } finally { Stop-Hold $script:flyProc; $script:flyProc = $null }
}

# ---------------------------------------------------------------- 5. flyout, grid layout + Explorer reveal

if (Should-Run 'flyout-grid') {
    try {
        $id = Ensure-Flyout 'flyout-grid' 150
        Test-UI "flyout-grid: the grid is up" { winapp ui wait-for 'Flyout_Grid' -a $id -t 5000 }
        Test-RowIds $id 'flyout-grid'
        Shot $id 'flyout-grid'
        Test-UI "flyout-grid: hover a tile reveals its actions" {
            winapp ui hover 'Flyout_RowOpen' -a $id --dwell-time 1200
            winapp ui wait-for 'Flyout_RowDelete' -a $id -t 3000
        }
        Shot $id 'flyout-grid-hover'
        $id = Ensure-Flyout 'flyout-grid' 150
        Test-A11y $id 'flyout-grid'

        # --- Flyout_RowFolder: does it reveal the file in Explorer? Last in the section, because Explorer
        #     taking the foreground dismisses the flyout.
        $id = Ensure-Flyout 'flyout-grid' 150
        # Explorer windows are identified by hwnd from the same Shell.Application enumerator the cleanup
        # uses, so the cleanup only closes windows this test opened.
        function Get-ExplorerWindows {
            $out = @{}
            try {
                $sh = New-Object -ComObject Shell.Application
                foreach ($w in @($sh.Windows())) {
                    try { if ($w.FullName -match 'explorer\.exe$') { $out[[string]$w.HWND] = $w.LocationName } } catch { }
                }
            } catch { Write-Note "could not enumerate Explorer windows: $_" }
            return $out
        }
        $explorerBefore = Get-ExplorerWindows
        Write-Note "Explorer windows open before the reveal: $($explorerBefore.Count)"
        $script:revealOpened = @()
        Test-UI "flyout: Flyout_RowFolder reveals the file in Explorer" {
            winapp ui hover 'Flyout_RowOpen' -a $id --dwell-time 1200 2>&1 | Out-Null
            winapp ui wait-for 'Flyout_RowFolder' -a $id -t 3000 2>&1 | Out-Null
            winapp ui invoke 'Flyout_RowFolder' -a $id
            if ($LASTEXITCODE -ne 0) { throw "invoke failed" }
            Start-Sleep -Seconds 5
            $after = Get-ExplorerWindows
            $new = @($after.Keys | Where-Object { -not $explorerBefore.ContainsKey($_) })
            if ($new.Count -eq 0) { throw "no new Explorer window appeared" }
            $script:revealOpened = $new
            $where = ($new | ForEach-Object { $after[$_] }) -join ' | '
            Write-Host "        new Explorer window(s): $where" -ForegroundColor DarkGray
            $script:notes += "Flyout_RowFolder opened Explorer at: $where"
        }
        # Close ONLY the windows this test is known to have opened, by hwnd.
        if ($script:revealOpened.Count -gt 0) {
            try {
                $sh = New-Object -ComObject Shell.Application
                foreach ($w in @($sh.Windows())) {
                    try {
                        if ($script:revealOpened -contains [string]$w.HWND) {
                            Write-Note "closing the Explorer window this test opened ('$($w.LocationName)')"
                            $w.Quit()
                        }
                    } catch { }
                }
            } catch { Write-Note "could not close the Explorer window: $_" }
        }
        Start-Sleep -Milliseconds 900
    } catch {
        $script:fail++; $script:results += @{ name = "flyout-grid section"; status = "FAIL"; detail = "$_" }
        Write-Host "  FAIL  flyout-grid section -- $_" -ForegroundColor Red
    } finally { Stop-Hold $script:flyProc; $script:flyProc = $null }
}

# ---------------------------------------------------------------- 6. overlay toolbar + the mode-switch recording

if (Should-Run 'toolbar') {
    $p = $null
    try {
        $p = Start-Hold 'toolbar' 170
        $id = $p.Id
        foreach ($el in 'Overlay_ModeRectangle','Overlay_ModeWindow','Overlay_ModeFreeform','Overlay_ModeFullScreen','Overlay_DelayPill','Overlay_AnnotateToggle','Overlay_CloseButton') {
            Test-UI "toolbar: $el exists" { winapp ui wait-for $el -a $id -t 4000 }
        }
        Test-UI "toolbar: Overlay_Modes is a named Group in the tree" {
            $pr = Get-Prop $id 'Overlay_Modes'
            if (-not $pr) { throw "Overlay_Modes is not in the UIA tree" }
            Write-Host "        Overlay_Modes type=$($pr.ControlType) name='$($pr.Name)'" -ForegroundColor DarkGray
            if ($pr.ControlType -notmatch 'Group') { throw "control type is $($pr.ControlType), not Group" }
            if (-not $pr.Name) { throw "the group has no Name" }
        }
        Test-UI "toolbar: the four mode buttons keep their names" {
            foreach ($m in 'Overlay_ModeRectangle','Overlay_ModeWindow','Overlay_ModeFullScreen','Overlay_ModeFreeform') {
                $pr = Get-Prop $id $m
                if (-not $pr) { throw "$m is gone" }
                if (-not $pr.Name) { throw "$m has no Name" }
            }
        }
        Shot $id 'toolbar'

        # Six seconds of screen recording while three modes are picked, to review the selection indicator.
        # --capture-screen, because the bar is a no-activate popup.
        $clip = Join-Path $OutDir 'overlay-mode-switch.mp4'
        Test-UI "toolbar: record a mode switch (6 s)" {
            $rec = Start-Process -FilePath 'winapp' -ArgumentList @('ui','record','-a',"$id",'--duration-sec','6','--fps','15','--capture-screen','-o',"$clip") -PassThru -WindowStyle Hidden
            Start-Sleep -Milliseconds 1200
            winapp ui invoke 'Overlay_ModeWindow' -a $id | Out-Null
            Start-Sleep -Milliseconds 1400
            winapp ui invoke 'Overlay_ModeFreeform' -a $id | Out-Null
            Start-Sleep -Milliseconds 1400
            winapp ui invoke 'Overlay_ModeRectangle' -a $id | Out-Null
            $rec | Wait-Process -Timeout 45
            if (-not (Test-Path $clip)) { throw "no clip at $clip" }
            $len = (Get-Item $clip).Length
            Write-Host "        $clip ($len bytes)" -ForegroundColor DarkGray
            if ($len -lt 2000) { throw "the clip is $len bytes" }
        }

        Test-UI "toolbar: the mode selection follows the click" {
            winapp ui invoke 'Overlay_ModeWindow' -a $id
            Start-Sleep -Milliseconds 700
            winapp ui wait-for 'Overlay_ModeWindow' -a $id -p ToggleState --value 'On' -t 3000
            if ($LASTEXITCODE -ne 0) { winapp ui wait-for 'Overlay_ModeWindow' -a $id -p IsSelected --value 'True' -t 3000 }
        }
        winapp ui invoke 'Overlay_ModeRectangle' -a $id | Out-Null
        Start-Sleep -Milliseconds 500

        Test-UI "toolbar: the delay pill opens its band" {
            winapp ui invoke 'Overlay_DelayPill' -a $id
            Start-Sleep -Milliseconds 800
            winapp ui wait-for 'Overlay_Delay3' -a $id -t 3000
        }
        Shot $id 'toolbar-delay'
        Test-UI "toolbar: Overlay_DelayBand is a named Group in the tree" {
            $pr = Get-Prop $id 'Overlay_DelayBand'
            if (-not $pr) { throw "Overlay_DelayBand is not in the UIA tree" }
            Write-Host "        Overlay_DelayBand type=$($pr.ControlType) name='$($pr.Name)'" -ForegroundColor DarkGray
            if ($pr.ControlType -notmatch 'Group') { throw "control type is $($pr.ControlType), not Group" }
            if (-not $pr.Name) { throw "the group has no Name" }
        }
        Test-UI "toolbar: hover the delay pill" { winapp ui hover 'Overlay_DelayPill' -a $id --dwell-time 1200 }
        Shot $id 'toolbar-delaypill-hover'
        Move-PointerAway
        Test-A11y $id 'toolbar'
    } catch {
        $script:fail++; $script:results += @{ name = "toolbar section"; status = "FAIL"; detail = "$_" }
        Write-Host "  FAIL  toolbar section -- $_" -ForegroundColor Red
    } finally { Stop-Hold $p }
}

# ---------------------------------------------------------------- 7. overlay toolbar with the tool row (bands)

function Get-BarWidth {
    param([int]$Target)
    $ws = Get-Json @('list-windows','-a',"$Target",'--json')
    $w = @($ws | Where-Object { $_.title -ne 'PopupHost' })[0]
    if ($w.width) { return [int]$w.width }
    if ($w.rect)  { return [int]($w.rect.right - $w.rect.left) }
    return 0
}

if (Should-Run 'toolbar-annotate') {
    $p = $null
    try {
        $p = Start-Hold 'toolbar-annotate' 190
        $id = $p.Id
        # Tools_DoneButton is not checked: only the editor's annotate flow shows it.
        foreach ($el in 'Tools_Pen','Tools_Arrow','Tools_ColourPicker','Tools_WidthPicker','Tools_ExposurePicker','Tools_ZebraToggle') {
            Test-UI "toolbar-annotate: $el exists" { winapp ui wait-for $el -a $id -t 4000 }
        }
        Shot $id 'toolbar-annotate'

        # The standing check: no band may widen the row.
        $w0 = Get-BarWidth $id
        Write-Note "toolbar-annotate width $w0"

        Test-UI "toolbar-annotate: the colour band opens" {
            winapp ui invoke 'Tools_ColourPicker' -a $id
            Start-Sleep -Milliseconds 800
            winapp ui wait-for 'Tools_SwatchRed' -a $id -t 3000
        }
        Shot $id 'toolbar-colour'
        $w1 = Get-BarWidth $id
        Test-UI "toolbar-annotate: the colour band does not widen the row ($w0 -> $w1)" {
            if ($w1 -gt $w0) { throw "the row grew from $w0 to $w1" }
        }

        Test-UI "toolbar-annotate: the exposure band opens" {
            winapp ui invoke 'Tools_ExposurePicker' -a $id
            Start-Sleep -Milliseconds 800
            winapp ui wait-for 'Tools_ExposureSlider' -a $id -t 3000
        }
        Shot $id 'toolbar-exposure'
        $w2 = Get-BarWidth $id
        Test-UI "toolbar-annotate: the exposure band does not widen the row ($w0 -> $w2)" {
            if ($w2 -gt $w0) { throw "the row grew from $w0 to $w2" }
        }

        # Does a ToolTip escape the overlay's content island?
        Test-UI "toolbar-annotate: hover a tool raises a tooltip outside the island" {
            $before = @(Get-Json @('list-windows','-a',"$id",'--json')).Count
            winapp ui hover 'Tools_Pen' -a $id --dwell-time 1800 | Out-Null
            Start-Sleep -Milliseconds 500
            $after = @(Get-Json @('list-windows','-a',"$id",'--json')).Count
            Write-Host "        windows $before -> $after during the hover" -ForegroundColor DarkGray
            if ($after -le $before) { throw "no tooltip window appeared ($before -> $after)" }
        }
        Shot $id 'toolbar-tooltip'
        Move-PointerAway
        Test-A11y $id 'toolbar-annotate'
    } catch {
        $script:fail++; $script:results += @{ name = "toolbar-annotate section"; status = "FAIL"; detail = "$_" }
        Write-Host "  FAIL  toolbar-annotate section -- $_" -ForegroundColor Red
    } finally { Stop-Hold $p }
}

# ---------------------------------------------------------------- 8. countdown

if (Should-Run 'countdown') {
    $p = $null
    try {
        $p = Start-Hold 'countdown' 45
        $id = $p.Id
        Test-UI "countdown: the digit is up" { winapp ui wait-for 'Countdown_Digit' -a $id -t 5000 }
        # get-value reads the bare digit; a screen reader announces the Name, which Set() updates so the
        # live region has a property change to raise.
        $n0 = Get-Prop $id 'Countdown_Digit' 'Name'
        Shot $id 'countdown'
        Test-UI "countdown: the name follows the value (the live region has something to announce)" {
            Start-Sleep -Seconds 2
            $n1 = Get-Prop $id 'Countdown_Digit' 'Name'
            Write-Host "        Name '$n0' -> '$n1'" -ForegroundColor DarkGray
            if (-not $n1) { throw "Countdown_Digit has no Name" }
            if ($n1 -notmatch 'second') { throw "the name is '$n1', not a spoken 'N seconds'" }
            if ($n1 -eq $n0) { throw "the name did not change from '$n0' in two seconds" }
        }
        # No Test-A11y: the pill has no interactive control, so `inspect --interactive` returns nothing.
        # The audit's checks are applied to the one element directly.
        Test-UI "countdown: the digit is a named, live-region element" {
            $pr = Get-Prop $id 'Countdown_Digit'
            if (-not $pr) { throw "Countdown_Digit is not in the tree" }
            if (-not $pr.Name) { throw "no Name" }
            if ([int][char]$pr.Name[0] -ge 0xE000 -and [int][char]$pr.Name[0] -le 0xF8FF) { throw "the Name starts with a private-use glyph" }
            Write-Host "        Countdown_Digit Name '$($pr.Name)' type $($pr.ControlType)" -ForegroundColor DarkGray
        }
    } catch {
        $script:fail++; $script:results += @{ name = "countdown section"; status = "FAIL"; detail = "$_" }
        Write-Host "  FAIL  countdown section -- $_" -ForegroundColor Red
    } finally { Stop-Hold $p }
}

# ---------------------------------------------------------------- 9. text entry + the IsPassword probe

if (Should-Run 'textentry') {
    $p = $null
    try {
        $p = Start-Hold 'textentry' 100
        $id = $p.Id
        Test-UI "textentry: the box is up and named" { winapp ui wait-for 'TextEntry_Box' -a $id -t 5000 }
        Shot $id 'textentry-empty'
        Test-UI "textentry: typing lands in the box" {
            winapp ui send-keys 'Clipped highlight' --target 'TextEntry_Box' -a $id --via send-input | Out-Null
            Start-Sleep -Milliseconds 900
            winapp ui wait-for 'TextEntry_Box' -a $id --value 'Clipped' --contains -t 4000
        }
        Shot $id 'textentry-text'
        Test-UI "textentry: the box holds the keyboard focus (as far as a hold can show it)" {
            $pr = Get-Prop $id 'TextEntry_Box'
            if (-not $pr) { throw "TextEntry_Box is not in the tree" }
            Write-Host "        HasKeyboardFocus=$($pr.HasKeyboardFocus) IsKeyboardFocusable=$($pr.IsKeyboardFocusable)" -ForegroundColor DarkGray
            if ("$($pr.HasKeyboardFocus)" -ne 'True') { throw "the box does not hold keyboard focus" }
        }

        # TextEntry_Box's properties are recorded for the IsPassword probe; the 'gallery' section is the
        # stock TextBox comparison.
        $probe = Get-Prop $id 'TextEntry_Box'
        $probe | ConvertTo-Json -Depth 6 | Out-File (Join-Path $OutDir 'probe-textentry.json') -Encoding ascii
        Write-Note ("IsPassword probe: TextEntry_Box type={0} class={1} IsPassword={2}" -f $probe.ControlType, $probe.ClassName, $probe.IsPassword)
        Test-A11y $id 'textentry'
    } catch {
        $script:fail++; $script:results += @{ name = "textentry section"; status = "FAIL"; detail = "$_" }
        Write-Host "  FAIL  textentry section -- $_" -ForegroundColor Red
    } finally { Stop-Hold $p }
}

# ---------------------------------------------------------------- 10. toast card

if (Should-Run 'toast') {
    $p = $null
    try {
        $p = Start-Hold 'toast-saved' 120
        $id = $p.Id
        foreach ($el in 'Toast_Card','Toast_Open','Toast_Folder','Toast_Edit','Toast_Close') {
            Test-UI "toast: $el exists" { winapp ui wait-for $el -a $id -t 4000 }
        }
        Shot $id 'toast-saved'
        Test-UI "toast: the card's name says what happened" {
            $v = Get-Prop $id 'Toast_Card' 'Name'
            Write-Host "        '$v'" -ForegroundColor DarkGray
            if ($v -notmatch 'Snip') { throw "the card is named '$v'" }
        }
        Test-A11y $id 'toast'

        # The link colour through rest, hover and press. Bounds are recorded so the crop is exact.
        $linkBounds = (Get-Prop $id 'Toast_Open' 'BoundingRectangle')
        Write-Note "Toast_Open bounds $linkBounds (theme $Theme)"
        Shot $id "toast-link-rest-$Theme"
        Test-UI "toast: the links survive a hover" {
            winapp ui hover 'Toast_Open' -a $id --dwell-time 1500
            if ($LASTEXITCODE -ne 0) { throw "hover failed" }
        }
        Shot $id "toast-link-hover-$Theme"
        Move-PointerAway
        Start-Sleep -Milliseconds 600
        Shot $id "toast-link-rest2-$Theme"

        Test-UI "toast: Toast_Close dismisses the card" {
            winapp ui invoke 'Toast_Close' -a $id
            if ($LASTEXITCODE -ne 0) { throw "invoke failed" }
            Start-Sleep -Seconds 3
            # The hold ends with the card, so the process exiting is the dismissal, not an error.
            if ($p.HasExited) { Write-Host "        the hold ended with the card" -ForegroundColor DarkGray; return }
            winapp ui wait-for 'Toast_Card' -a $id --gone -t 6000
        }
    } catch {
        $script:fail++; $script:results += @{ name = "toast section"; status = "FAIL"; detail = "$_" }
        Write-Host "  FAIL  toast section -- $_" -ForegroundColor Red
    } finally { Stop-Hold $p }
}

# ---------------------------------------------------------------- 11. toast dwell: hover pauses, an injected move resumes

if (Should-Run 'toast-dwell') {
    $p = $null
    try {
        $p = Start-Hold 'toast-dwell' 45
        $id = $p.Id
        Test-UI "toast-dwell: the card is up" { winapp ui wait-for 'Toast_Card' -a $id -t 5000 }
        Test-UI "toast-dwell: hovering holds the card past its five seconds" {
            winapp ui hover 'Toast_Card' -a $id --dwell-time 8000 | Out-Null
            winapp ui wait-for 'Toast_Card' -a $id -t 2000
            if ($LASTEXITCODE -ne 0) { throw "the card went away while the pointer was on it" }
        }
        Test-UI "toast-dwell: an injected move away lets the dwell finish" {
            Move-PointerAway
            for ($i = 0; $i -lt 14; $i++) {
                if ($p.HasExited) { Write-Host "        the card closed $i s after the pointer left" -ForegroundColor DarkGray; return }
                Start-Sleep -Seconds 1
            }
            winapp ui wait-for 'Toast_Card' -a $id --gone -t 4000
        }
    } catch {
        $script:fail++; $script:results += @{ name = "toast-dwell section"; status = "FAIL"; detail = "$_" }
        Write-Host "  FAIL  toast-dwell section -- $_" -ForegroundColor Red
    } finally { Stop-Hold $p }
}

# ---------------------------------------------------------------- 12. the stock-TextBox IsPassword control probe

if ((Should-Run 'gallery') -and -not $SkipGallery) {
    $g = $null
    try {
        Write-Host "`n=== WinUI 3 Gallery (stock TextBox control probe)" -ForegroundColor Cyan
        $before = @(Get-Process -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
        # The Gallery's own deep link, so a stock TextBox is on screen without navigating it by hand.
        Start-Process 'winui3gallery://item/TextBox'
        for ($i = 0; $i -lt 40 -and -not $g; $i++) {
            Start-Sleep -Milliseconds 750
            $g = Get-Process -ErrorAction SilentlyContinue |
                 Where-Object { $before -notcontains $_.Id -and
                                ($_.ProcessName -match 'Gallery' -or $_.MainWindowTitle -match 'Gallery') } |
                 Select-Object -First 1
        }
        if ($g) { Write-Note "gallery process $($g.ProcessName) pid $($g.Id) '$($g.MainWindowTitle)'" }
        if (-not $g) { throw "the WinUI 3 Gallery did not start" }
        Start-Sleep -Seconds 5
        $els = @(Get-Elements $g.Id)
        $edits = @($els | Where-Object { $_.type -match 'Edit' })
        Test-UI "gallery: a stock WinUI TextBox is in the tree" {
            if ($edits.Count -eq 0) { throw "no Edit control found in the Gallery" }
        }
        $rows = @()
        foreach ($e in $edits) {
            $sel = if ($e.automationId) { $e.automationId } elseif ($e.slug) { $e.slug } else { $e.name }
            if (-not $sel) { continue }
            $pr = Get-Prop $g.Id "$sel"
            if ($pr) { $rows += [pscustomobject]@{ selector = $sel; type = $pr.ControlType; className = $pr.ClassName; isPassword = $pr.IsPassword } }
        }
        $rows | ConvertTo-Json -Depth 5 | Out-File (Join-Path $OutDir 'probe-gallery-textbox.json') -Encoding ascii
        foreach ($r in $rows) { Write-Note ("stock probe: {0} type={1} class={2} IsPassword={3}" -f $r.selector, $r.type, $r.className, $r.isPassword) }
        Test-UI "gallery: at least one stock TextBox reported IsPassword" {
            if ($rows.Count -eq 0) { throw "no property read back" }
        }
    } catch {
        $script:fail++; $script:results += @{ name = "gallery probe"; status = "FAIL"; detail = "$_" }
        Write-Host "  FAIL  gallery probe -- $_" -ForegroundColor Red
    } finally {
        if ($g -and -not $g.HasExited) { Stop-Process -Id $g.Id -Force -ErrorAction SilentlyContinue }
    }
}

# ---------------------------------------------------------------- results

Write-Host "`n================================================" -ForegroundColor White
Write-Host "Passed: $script:pass | Failed: $script:fail" -ForegroundColor White
$script:results | Where-Object { $_.status -eq 'FAIL' } | ForEach-Object {
    Write-Host "  FAIL: $($_.name) -- $($_.detail)" -ForegroundColor Red
}
@{ passed = $script:pass; failed = $script:fail; results = $script:results; notes = $script:notes } |
    ConvertTo-Json -Depth 6 | Out-File (Join-Path $OutDir 'test-results.json') -Encoding ascii
Write-Host "results  $(Join-Path $OutDir 'test-results.json')"
Write-Host "shots    $ShotDir"
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
