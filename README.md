# ToneSnip

A snipping tool for HDR desktops on Windows 11.

With HDR on, Print Screen and the built-in Snipping Tool read an 8-bit SDR view of a floating-point framebuffer:
HDR content clips, colours shift, and ordinary windows come out washed out or overexposed. ToneSnip captures each
monitor through Windows.Graphics.Capture in 16-bit float scRGB and tonemaps it using the SDR white level Windows
reports for that monitor. Content at or below SDR white comes out the same as a non-HDR screenshot; only HDR
highlights are compressed. What you paste looks like what you saw.

## Features

- **Snips:** rectangle, window, full-screen and freeform, over a frozen preview of every monitor. A toolbar switches
  mode mid-snip and sets a 3, 5 or 10 second delay. A selection can span monitors, mixing HDR and SDR displays.
- **Nits readout** on HDR monitors: luminance under the cursor, plus peak and mean inside the selection.
- **Selection frame**, picked in Settings: Normal (an outline, with the size and nits beside the cursor), Viewfinder
  (accent corner brackets and a size label), or Guides (the selection's edges carried across the monitor, and a pixel
  loupe by the cursor showing its coordinates and nits).
- **Tonemapping:** three curves (a hue-preserving desktop clip with an optional BT.2390 knee, Hable, ACES), exposure,
  per-monitor SDR white detection and optional auto exposure per snip. On HDR snips, exposure stays live in the
  overlay and in the save-then-edit flow, with a zebra overlay showing what clips.
- **Annotate** in the overlay or in the editor: pen, highlighter, line, arrow, rectangle, ellipse, text, numbered
  counters, blur and pixelate, with undo and redo. Crop in the editor.
- **Privacy mode** (on by default): blur and pixelate paint a generated pattern in the region's overall tone instead
  of filtering the real pixels, so a redaction cannot be reversed with tools like Depix.
- **Output:** copies to the clipboard (DIB and PNG), saves to `Pictures\Screenshots` as PNG or JPEG, and shows a
  notification with a thumbnail and Open, Edit and Show in folder. The notification is ToneSnip's own card by
  default, or a Windows notification.
- **Optional HDR copy** next to every snip: JPEG XR for Windows Photos, a 16-bit PNG with cICP for browsers, or a
  gain-map JPEG that is SDR everywhere and HDR in Chrome, Android and Apple Photos. It carries the same crop,
  annotations and redactions as the SDR file.
- **Recent snips:** left-click the tray icon for new-snip buttons and the last 20 snips, as a list or a grid, with
  copy, open, edit, show in folder and delete (to the Recycle Bin by default).
- **Hotkeys** recorded in Settings, including PrintScreen itself and, optionally, `Win+Shift+S` in place of Snipping
  Tool.
- **Settings** with a live tonemap preview. Follows the Windows theme, accent colour and contrast themes, and is
  per-monitor DPI aware.
- Falls back to GDI for a monitor that gives no frame (some VMs and Remote Desktop sessions).

## Requirements

- Windows 11. ToneSnip turns off the yellow capture border, which Windows 10 does not allow.
- For `tonesnip.exe` only: the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). The MSIX
  is self-contained.

## Install

Both files are on the [latest release](https://github.com/merson316/tonesnip/releases/latest).

### MSIX (recommended)

The package is signed with a self-signed certificate, so Windows has to be told to trust it once:

1. Download `tonesnip.cer` from the release.
2. From an elevated Command Prompt in that folder, run `certutil -addstore TrustedPeople tonesnip.cer`. Or
   double-click the file, choose **Install Certificate**, **Local Machine**, **Place all certificates in the
   following store**, and pick **Trusted People**.
3. Double-click `tonesnip-x64.msix` and choose **Install**.

Later releases install over the top and keep your settings and history. To uninstall, use **Settings > Apps >
Installed apps > ToneSnip**; this leaves `%LOCALAPPDATA%\tonesnip` behind. Trusting the certificate trusts anything
signed with it; remove it with `certutil -delstore TrustedPeople merson316`.

### Single exe

Put `tonesnip.exe` somewhere permanent and run it. It carries the Windows App SDK, so only the .NET 10 Desktop Runtime
is needed.

Either way, a tray icon appears. To start with Windows, turn on **Start with Windows** in Settings > General.

## Keys

| Default hotkey | Action |
|---|---|
| `PrintScreen` | Region snip |
| `Ctrl+PrintScreen` | Window snip |
| `Shift+PrintScreen` | All monitors, instantly |
| `Alt+PrintScreen` | Active window, instantly |
| `Ctrl+Shift+PrintScreen` | Recent snips |
| `Win+Shift+S` | Off by default; turn on "Also use Win + Shift + S for region snips" in Settings > Hotkeys |

**Overlay:** `R` rectangle, `W` window, `F` full screen, `L` freeform; arrow keys nudge the selection (`Shift` for
10 px); `Enter` accepts; `Esc` or right-click cancels; `A` toggles annotating.

**Editor:** `Ctrl+C` copy, `Ctrl+S` save, `Ctrl+Shift+S` save as, `Ctrl+0` fit to window, mouse wheel zooms,
`Space`+drag or middle-drag pans, `A` toggles annotating, `Esc` closes.

**Annotating:** `V` select, `P` pen, `H` highlighter, `I` line, `O` arrow, `Ctrl+R` rectangle, `E` ellipse, `T` text,
`N` counter, `B` blur, `X` pixelate, `C` crop (editor), `Ctrl+Z` / `Ctrl+Y` undo and redo, `Delete` removes the
selected shape. `Esc` closes the text box, then clears the selection, then cancels.

**Command line:** a running ToneSnip takes these from a second launch.

```
tonesnip.exe --snip region|window|fullscreen|freeform|fullScreenAll|activeWindow [--delay <seconds>]
tonesnip.exe --settings
tonesnip.exe --history
tonesnip.exe --help
```

## Settings

Settings are edited from the tray and stored in `%LOCALAPPDATA%\tonesnip\settings.json`. A file that cannot be read
is kept as `settings.json.bad-<time>` and defaults are used.

| Field | Default | Notes |
|---|---|---|
| `hotkeys.region` `.window` `.fullScreenAll` `.activeWindow` `.history` | see above | Chords such as `Ctrl+Shift+F9`; empty is unbound |
| `hotkeys.replaceSnippingTool` | `false` | Also binds `Win+Shift+S` to a region snip |
| `tonemap` | `desktop` | `desktop`, `hable` or `aces` |
| `exposure` | `1.0` | Linear multiplier before tonemapping |
| `knee` | `1.0` | Desktop curve: 1 clips at SDR white; lower starts a BT.2390 roll-off at that fraction |
| `sdrWhiteNits` / `peakNits` | `null` | Override what the monitor reports |
| `autoExposure` | `false` | Maps the 99th-percentile luminance of the snip to SDR white |
| `copyToClipboard` | `true` | |
| `autoSave` / `saveFolder` | `true` / `null` | `null` is `Pictures\Screenshots`; files are `Snip yyyy-MM-dd HHmmss.png` |
| `format` / `jpegQuality` | `png` / `90` | `png` or `jpeg` |
| `showToast` / `notification` | `true` / `tonesnip` | `tonesnip` (the app's card) or `windows` |
| `afterSelect` | `save` | `save`, `annotate` (draw in the overlay, Enter saves), `annotateFirst` (overlay opens with the pen), `edit` (save, then open the editor) |
| `annotate.colour` `.width` `.textSize` | `accent` / `4` / `20` | Last-used style; `#RRGGBB` or `accent` |
| `annotate.privacyMode` | `true` | Generated pattern for blur and pixelate |
| `annotate.clipToLasso` | `true` | Freeform snips cut annotations outside the lasso |
| `hdr.file` | `none` | `none`, `jxr`, `png` (`.hdr.png`) or `jpeg` (`.hdr.jpg` with a gain map) |
| `hdr.jxrLossless` / `hdr.jxrQuality` | `true` / `90` | |
| `defaultDelay` | `0` | 0, 3, 5 or 10 seconds |
| `theme` | `auto` | `auto`, `light` or `dark` |
| `trayIcon` | `mono` | `mono`, `colour` or `accent` |
| `recentFlyoutLayout` | `row` | `row` or `grid` |
| `deleteToRecycleBin` | `true` | Deleting from Recent snips moves the file and its HDR copy to the Recycle Bin |
| `showNitsReadout` | `true` | |
| `startWithWindows` | `false` | A per-user Run key; the MSIX uses its startup task instead |

## How it works

```
hotkey / tray / --snip ──▶ tonesnip.exe
                              │ Windows.Graphics.Capture, one frame per monitor, in parallel
                              │   FP16 for an HDR monitor, BGRA8 for an SDR one (GDI if a monitor gives none)
                              ▼
                           tonemap ─▶ overlay ─▶ crop/composite ─▶ clipboard · file · notification · editor
```

- **Capture on demand.** Nothing is captured between snips. Each snip opens a capture session per monitor, takes one
  frame and closes it. One graphics device serves monitors on any GPU and is released a minute after the last snip.
  Capture works while the displays are asleep.
- **One process, light at rest.** The UI is WinUI 3; the overlay is plain Win32 and GDI, and the editor draws through
  Win2D. Frames stay half-float, a finished snip is kept as PNG, and frame buffers are released a minute after the
  last snip.
- **Fast tonemapping.** ACES is a per-channel curve, so it becomes a 64 KB table indexed by the FP16 bit pattern. The
  hue-preserving desktop curve stays in float and finishes through a 4096-entry sRGB table.
- **One rasterizer.** Annotations are a small document in the capture's pixel frame. The overlay, the editor and the
  saved file all draw it through the same GDI+ code, so what you see is what is saved.
- **The HDR copy is composited in linear light.** The SDR composite is lifted to the snip's reference white, the
  captured half-float crops replace it where the desktop was HDR, redactions run on the half-float data with the
  same privacy rule, and annotations are blended so a white pen stroke is exactly SDR white.

## Privacy

- ToneSnip installs a low-level keyboard hook to see its hotkeys. Each key-down is compared with the configured chords;
  keys are never stored or logged.
- Frames live in memory only. It writes the snips you take to your save folder, and a history of the last 20
  (`%LOCALAPPDATA%\tonesnip\history.json`, with a thumbnail each under `thumbs\`).
- It makes no network connections. The log is `%LOCALAPPDATA%\tonesnip\tonesnip.log`.
- Privacy mode reads one mean colour for a redacted region plus a per-shape seed, never the pixels underneath. Turn
  it off in Settings for classic blur and pixelate, which do filter the real pixels. It applies to the HDR copy too.

## Building

`ToneSnip.Core`, `ToneSnip.Windows` and the tests build with any .NET 10 SDK, Linux and WSL included. The app itself
needs the Windows .NET 10 SDK for its WinUI tooling.

```
dotnet test tests/ToneSnip.Core.Tests
dotnet publish src/ToneSnip -c Release -r win-x64     # on Windows
tools/publish.sh [exe|debug|msix]                     # from WSL, through the Windows SDK
```

`tools/publish.sh` builds into `dist/`:

- `exe` (the default): the single-file production `tonesnip.exe`. On WSL it is also installed to
  `%USERPROFILE%\Tools\tonesnip` and started; set `TONESNIP_NO_INSTALL=1` to only build.
- `msix`: `tonesnip-x64.msix`, signed when `TONESNIP_PFX` and `TONESNIP_PFX_PASSWORD` are set, unsigned otherwise.
- `debug`: `tonesnip-debug.exe`, the same app built with `-p:ToneSnipHarness=true`. It adds `--selftest`,
  `--screenshots`, `--hold` (for the UI Automation pass in `tools/ui-tests`), `--leaktest` and `--memtest`, and logs at
  debug level. It uses its own mutex, pipe and `%LOCALAPPDATA%\tonesnip-debug` folder, so it runs beside an installed
  ToneSnip without touching its data. None of the harness code is compiled into the production exe.

The version is `<Version>` in `src/ToneSnip/ToneSnip.csproj`; the build stamps it into the MSIX manifest. The app icons
in `assets/` are generated from `assets/icon.svg` with `python3 tools/gen-assets.py`.

### Releases and signing

Pushing a `v*` tag runs `.github/workflows/windows.yml`, which tests, builds and smoke-tests the exe and the MSIX and
attaches them to a GitHub release.

`packaging/make-cert.ps1` creates the sideload certificate once. It writes `tonesnip.pfx` (the private key) and
`tonesnip.cer` (the public certificate) into `packaging/`.

- **Never commit `tonesnip.pfx`** (`*.pfx` is ignored). Store it base64-encoded in the `MSIX_PFX_BASE64` repository
  secret and its password in `MSIX_PFX_PASSWORD`; the workflow decodes it for the signing step and deletes it after.
- **Commit `tonesnip.cer`.** It is public, and the release attaches it for users to trust.

### Layout

| Path | Contents |
|---|---|
| `src/ToneSnip.Core` | Colour maths, tonemappers, geometry, compositing, HDR encoding, annotations, settings; cross-platform and unit-tested |
| `src/ToneSnip.Windows` | Windows.Graphics.Capture with [Vortice](https://github.com/amerkoleci/Vortice.Windows), DisplayConfig, keyboard hook, tray, GDI and WIC |
| `src/ToneSnip` | The WinUI 3 app |
| `tests/ToneSnip.Core.Tests` | xUnit tests for Core |
| `tools/ui-tests` | The UI Automation pass, run against the debug build |
| `packaging` | MSIX manifest and certificate script |

## Limitations

- Games in exclusive fullscreen are untested; borderless windowed works. DRM-protected content captures black.
- The clipboard carries SDR only; no application reads an HDR clipboard format.
- No video recording.

## Credits

- [HDRSnip](https://github.com/mattcam98/HDRSnip) by mattcam98, an independent MIT project with the same goal, for the
  FP16 lookup-table and GDI-fallback ideas.
- ToneSnip grew out of a ShareX helper, [sharex-hdr2sdr](https://github.com/merson316/sharex-hdr2sdr).

## License

MIT; see [LICENSE](LICENSE).
