using System.Runtime.InteropServices;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Windows.Imaging;
using ToneSnip.Windows.Interop;

namespace ToneSnip.Windows.Tray;

/// <summary>
/// The tray context menu as a plain Win32 popup, owner-drawn in the app's theme tokens (26 px rows, 7 px vertical
/// padding, 4 px hover radius) without loading any XAML.
/// <para>
/// In a contrast theme every colour comes from <c>GetSysColor</c> instead (see <see cref="Palette"/>); geometry and
/// text rendering are unchanged.
/// </para>
/// </summary>
public sealed partial class TrayMenu
{
    private enum Kind { Item, Header, Separator, Spacer }

    private sealed record Entry(Kind Kind, string Text, Action? Action, Func<bool>? Checked, Func<string?>? Accelerator);

    // Geometry at 96 dpi, scaled per monitor in Show().
    private const int RowHeight = 26, PadHeight = 7, SeparatorHeight = 7, TextX = 36, RightPad = 12, AccelGap = 24, CheckX = 12, HoverInsetX = 3, HoverInsetY = 1, CornerRadius = 4, MenuMinWidth = 220;
    private const string CheckGlyph = "\uE73E";   // Segoe Fluent Icons "Accept"
    // Em sizes in pixels at 96 dpi, scaled per monitor: Body 14 for items, Caption 12 for headers and accelerators.
    private const float ItemFontPx = 14f, SmallFontPx = 12f, IconFontPx = 12f;
    private const string UiFace = "Segoe UI Variable Text", IconFace = "Segoe Fluent Icons";

    private readonly MessageWindow _window;
    private readonly ILog _log;
    private readonly List<Entry> _items = new();

    /// <summary>Theme inputs, supplied by the app (the Windows library never references WinUI).</summary>
    public Func<bool> IsDark { get; set; } = () => true;

    /// <summary>
    /// Whether the desktop is in a contrast theme, which overrides <see cref="IsDark"/> entirely. Defaults to asking
    /// Windows directly; the app points it at <c>ThemeManager.IsHighContrast</c> so XAML and the menu agree.
    /// </summary>
    public Func<bool> IsHighContrast { get; set; } = SystemTheme.IsHighContrast;

    public TrayMenu(MessageWindow window, ILog log)
    {
        _window = window;
        _log = log;
    }

    public void Add(string text, Action action, Func<bool>? isChecked = null, Func<string?>? accelerator = null) => _items.Add(new Entry(Kind.Item, text, action, isChecked, accelerator));
    public void AddHeader(string text) => _items.Add(new Entry(Kind.Header, text, null, null, null));
    public void AddSeparator() => _items.Add(new Entry(Kind.Separator, string.Empty, null, null, null));

    #region interop

    [StructLayout(LayoutKind.Sequential)] private struct MeasureItem { public uint CtlType, CtlId, ItemId, ItemWidth, ItemHeight; public IntPtr ItemData; }
    [StructLayout(LayoutKind.Sequential)] private struct DrawItem { public uint CtlType, CtlId, ItemId, ItemAction, ItemState; public IntPtr HwndItem, Dc; public User32.Rect Item; public IntPtr ItemData; }
    [StructLayout(LayoutKind.Sequential)] private struct MenuInfo { public int CbSize, Mask, Style, CyMax; public IntPtr Background; public int ContextHelpId; public IntPtr MenuData; }

    [LibraryImport("user32.dll")] private static partial IntPtr CreatePopupMenu();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyMenu(IntPtr menu);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MenuItemInfo
    {
        public uint CbSize, Mask, Type, State, Id;
        public IntPtr SubMenu, BmpChecked, BmpUnchecked;
        public UIntPtr ItemData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TypeData;
        public uint Cch;
        public IntPtr BmpItem;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool InsertMenuItemW(IntPtr menu, uint position, bool byPosition, ref MenuItemInfo item);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetMenuInfo(IntPtr menu, ref MenuInfo info);
    [LibraryImport("user32.dll")] private static partial int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);

    private const uint MiimState = 0x1, MiimId = 0x2, MiimData = 0x20, MiimString = 0x40, MiimFType = 0x100;
    private const uint MftOwnerDraw = 0x100, MfsGrayed = 0x3, MfsChecked = 0x8;
    private const uint WmMenuChar = 0x0120;
    private const int MncExecute = 2, MncSelect = 3;
    private const int MimBackground = 0x0002, MimApplyToSubmenus = unchecked((int)0x80000000);
    private const uint TpmLeftAlign = 0x0000, TpmBottomAlign = 0x0020, TpmRightButton = 0x0002, TpmReturnCmd = 0x0100;
    private const uint OdtMenu = 1, OdsSelected = 0x0001;
    private const int NullPen = 8, MonitorDefaultToNearest = 2, SmCxMenuCheck = 71;
    private const uint SpiGetFontSmoothing = 0x004A, SpiGetFontSmoothingType = 0x200A;
    private const int FeFontSmoothingClearType = 2;
    /// <summary>GDI+ text gamma, 0-12, per theme; see <see cref="Context"/> for why the two differ.</summary>
    private const uint DarkTextGamma = 12, LightTextGamma = 4;

    #endregion

    // Live only while a menu is up; WM_MEASUREITEM and WM_DRAWITEM arrive inside TrackPopupMenuEx.
    private IntPtr _surfaceBrush, _hoverBrush, _separatorBrush;
    private int _scaleNumerator = 96, _menuWidth;
    /// <summary>The pass's colours, one per role. Opaque 0xAARRGGBB: GDI+ takes ARGB, not a COLORREF.</summary>
    private Palette _ink;
    /// <summary>The pass's fonts and formats, built by <see cref="BuildTheme"/> and released with the brushes.</summary>
    private Ink? _fonts;
    private bool _open;

    private int Scale(int px) => px * _scaleNumerator / 96;
    private float ScaleF(float px) => px * _scaleNumerator / 96f;

    /// <summary>Opens the menu at a screen point and runs the chosen item's action once it has closed.</summary>
    public void Show(int x, int y)
    {
        if (_open) return;
        _open = true;
        _highlighted = 0;
        IntPtr menu = IntPtr.Zero;
        int chosen = 0;
        try
        {
            // The hook is claimed only while the menu is up, since WM_MEASUREITEM/WM_DRAWITEM arrive only inside
            // TrackPopupMenuEx. Set inside the try because the setter throws if another hook holds the window.
            _window.Hook = OnMessage;
            _scaleNumerator = DpiAt(x, y);
            BuildTheme(IsHighContrast());
            MeasureItems();
            menu = Build();
            var info = new MenuInfo { CbSize = Marshal.SizeOf<MenuInfo>(), Mask = MimBackground | MimApplyToSubmenus, Background = _surfaceBrush };
            SetMenuInfo(menu, ref info);
            // The menu only dismisses on an outside click and takes the keyboard when its owner is the foreground
            // window; the trailing WM_NULL is the documented companion to that call.
            if (!User32.SetForegroundWindow(_window.Handle)) _log.Warn("tray menu: SetForegroundWindow failed; the menu may not dismiss on an outside click");
            chosen = TrackPopupMenuEx(menu, TpmLeftAlign | TpmBottomAlign | TpmRightButton | TpmReturnCmd, x, y, _window.Handle, IntPtr.Zero);
            User32.PostMessageW(_window.Handle, MessageWindow.WmNull, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            ReleaseTheme();
            if (_window.Hook == (Func<uint, IntPtr, IntPtr, IntPtr?>)OnMessage) _window.Hook = null;   // only our own
            _open = false;
        }
        // Run after the menu and its GDI objects are gone, since an action may open a window or quit the app. Show()
        // runs inside MessageWindow's window procedure (a reverse P/Invoke), so an action's exception must not escape.
        if (chosen > 0 && chosen <= _items.Count)
            try { _items[chosen - 1].Action?.Invoke(); }
            catch (Exception e) { _log.Error($"tray menu: \"{_items[chosen - 1].Text}\" failed: {e}"); }
    }

    private IntPtr Build()
    {
        IntPtr menu = CreatePopupMenu();
        // A disabled owner-drawn spacer at each end supplies the padding: the popup has no padding property, and the
        // first and last rows would otherwise touch the rounded corners.
        Append(menu, 0, EntryFor(0));
        for (int i = 0; i < _items.Count; i++) Append(menu, i + 1, _items[i]);
        Append(menu, _items.Count + 1, EntryFor(_items.Count + 1));
        return menu;
    }

    /// <summary>
    /// One owner-drawn row, with its text and checked state also set on the item so screen readers can announce them.
    /// The accelerator goes after a tab, which is where MSAA looks for a shortcut.
    /// </summary>
    private static void Append(IntPtr menu, int id, Entry e)
    {
        string? accel = e.Kind == Kind.Item ? e.Accelerator?.Invoke() : null;
        var info = new MenuItemInfo
        {
            CbSize = (uint)Marshal.SizeOf<MenuItemInfo>(),
            Mask = MiimFType | MiimId | MiimData | MiimState | (e.Kind is Kind.Item or Kind.Header ? MiimString : 0),
            // Not MFT_SEPARATOR on a separator: the rows are measured and painted here, and a separator type hands its
            // height back to the system.
            Type = MftOwnerDraw,
            State = (e.Kind == Kind.Item ? 0 : MfsGrayed) | (e.Checked?.Invoke() == true ? MfsChecked : 0),
            Id = (uint)id,
            ItemData = (UIntPtr)(uint)id,
            TypeData = e.Kind is Kind.Item or Kind.Header ? (string.IsNullOrEmpty(accel) ? e.Text : e.Text + "\t" + accel) : null,
        };
        InsertMenuItemW(menu, uint.MaxValue, true, ref info);   // position -1: the end
    }

    /// <summary>
    /// A letter typed while the menu is up; owner-drawn items get no mnemonic handling from Windows. A unique match is
    /// chosen; with several, the next match after the highlighted row is highlighted.
    /// </summary>
    private IntPtr? OnMenuChar(IntPtr wParam)
    {
        char c = char.ToUpperInvariant((char)((long)wParam & 0xFFFF));
        var matches = new List<int>();
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].Kind == Kind.Item && _items[i].Text.Length > 0 && char.ToUpperInvariant(_items[i].Text[0]) == c) matches.Add(i + 1);
        if (matches.Count == 0) return null;
        if (matches.Count == 1) return (IntPtr)((MncExecute << 16) | matches[0]);
        int next = matches.FirstOrDefault(m => m > _highlighted, matches[0]);
        return (IntPtr)((MncSelect << 16) | next);
    }

    /// <summary>The row drawn highlighted last, for <see cref="OnMenuChar"/> to cycle from.</summary>
    private int _highlighted;

    /// <summary>Index 0 and count+1 are the padding spacers; 1..count are the entries.</summary>
    private Entry EntryFor(int id) => id >= 1 && id <= _items.Count ? _items[id - 1] : new Entry(Kind.Spacer, string.Empty, null, null, null);

    /// <summary>Owner-draw and popup messages, called from inside <c>TrackPopupMenuEx</c>'s native modal loop: an
    /// exception here would unwind through user32, so a failed measure or paint degrades to the system default.</summary>
    private IntPtr? OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!_open) return null;
        try { return OnMessageCore(msg, wParam, lParam); }
        catch (Exception e) { _log.Error($"tray menu: msg 0x{msg:X4}: {e}"); return null; }
    }

    private IntPtr? OnMessageCore(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case MessageWindow.WmMeasureItem:
            {
                MeasureItem m = Marshal.PtrToStructure<MeasureItem>(lParam);
                if (m.CtlType != OdtMenu) return null;
                Entry e = EntryFor((int)m.ItemData);
                m.ItemWidth = (uint)_menuWidth;
                m.ItemHeight = (uint)Scale(e.Kind switch { Kind.Separator => SeparatorHeight, Kind.Spacer => PadHeight, _ => RowHeight });
                Marshal.StructureToPtr(m, lParam, false);
                return (IntPtr)1;
            }
            case MessageWindow.WmDrawItem:
            {
                DrawItem d = Marshal.PtrToStructure<DrawItem>(lParam);
                if (d.CtlType != OdtMenu) return null;
                Draw(d);
                return (IntPtr)1;
            }
            case WmMenuChar:
                return OnMenuChar(wParam);
            case MessageWindow.WmInitMenuPopup:
                // Ask DWM to round the popup window. #32768 is the menu window class.
                Dwm.RoundCorners(User32.FindWindowW("#32768", null));
                return null;
            default: return null;
        }
    }

    /// <summary>
    /// The fonts and formats one owner-draw pass needs, as GDI+ objects. Created in <see cref="BuildTheme"/> and released
    /// in <see cref="ReleaseTheme"/>; never kept across menus, whose DPI may differ.
    /// </summary>
    private sealed class Ink : IDisposable
    {
        private readonly GdiPlus.FontFamily _ui, _icons;
        public readonly GdiPlus.Font Item, Small, Icon;
        /// <summary>Left, right and centre, each vertically centred. The typographic preset, because the default adds a
        /// sixth of an em of padding either side and would shift text off its column.</summary>
        public readonly GdiPlus.StringFormat Left, Right, Centre;

        public Ink(float itemPx, float smallPx, float iconPx)
        {
            _ui = GdiPlus.FontFamily.TryCreate(UiFace) ?? GdiPlus.FontFamily.TryCreate("Segoe UI") ?? GdiPlus.FontFamily.GenericSansSerif();
            _icons = GdiPlus.FontFamily.TryCreate(IconFace) ?? GdiPlus.FontFamily.TryCreate("Segoe MDL2 Assets") ?? GdiPlus.FontFamily.GenericSansSerif();
            Item = new GdiPlus.Font(_ui, itemPx, GdiPlus.FontStyle.Regular);
            Small = new GdiPlus.Font(_ui, smallPx, GdiPlus.FontStyle.Regular);
            Icon = new GdiPlus.Font(_icons, iconPx, GdiPlus.FontStyle.Regular);
            Left = Format(GdiPlus.StringAlignment.Near);
            Right = Format(GdiPlus.StringAlignment.Far);
            Centre = Format(GdiPlus.StringAlignment.Center);
        }

        private static GdiPlus.StringFormat Format(GdiPlus.StringAlignment alignment)
        {
            GdiPlus.StringFormat f = GdiPlus.StringFormat.GenericTypographic();
            f.Alignment = alignment;
            f.LineAlignment = GdiPlus.StringAlignment.Center;
            return f;
        }

        public void Dispose()
        {
            Item.Dispose(); Small.Dispose(); Icon.Dispose();
            Left.Dispose(); Right.Dispose(); Centre.Dispose();
            _ui.Dispose(); _icons.Dispose();
        }
    }

    private Ink NewInk() => new(ScaleF(ItemFontPx), ScaleF(SmallFontPx), ScaleF(IconFontPx));

    /// <summary>
    /// A GDI+ context on the menu's DC, grid-fitted and ClearType when the desktop uses it.
    /// <para>
    /// <c>ClearTypeGridFit</c> rather than <c>AntiAliasGridFit</c>, which leaves stems on fractional x and blurs them
    /// at 12-14 px; the result is closer to WinUI's own text rendering.
    /// </para>
    /// <para>
    /// <see cref="GdiPlus.Graphics.TextContrast"/> is per theme because ClearType's gamma correction is not symmetric:
    /// raising it thickens light-on-dark text and thins dark-on-light.
    /// </para>
    /// </summary>
    private GdiPlus.Graphics Context(IntPtr dc)
    {
        GdiPlus.Graphics g = GdiPlus.Graphics.FromHdc(dc);
        // Respect the desktop's setting: with ClearType off, use the grayscale hint.
        g.TextRendering = ClearTypeEnabled() ? GdiPlus.TextRenderingHint.ClearTypeGridFit : GdiPlus.TextRenderingHint.AntiAliasGridFit;
        // Keyed on the surface colour, not the app setting, so contrast themes get the right gamma.
        g.TextContrast = _ink.LightOnDark ? DarkTextGamma : LightTextGamma;
        return g;
    }

    /// <summary>True when font smoothing is on and set to ClearType (the Windows default). An unanswered query counts
    /// as true.</summary>
    private static bool ClearTypeEnabled()
    {
        try
        {
            if (!User32.SystemParametersInfoW(SpiGetFontSmoothing, 0, out int on, 0) || on == 0) return false;
            return !User32.SystemParametersInfoW(SpiGetFontSmoothingType, 0, out int type, 0) || type == FeFontSmoothingClearType;
        }
        catch (EntryPointNotFoundException) { return true; }
    }

    /// <summary>One run of text, inside the box the caller measured.</summary>
    private static void Text(GdiPlus.Graphics g, string text, GdiPlus.Font font, GdiPlus.StringFormat format, uint argb, int left, int top, int right, int bottom)
    {
        using GdiPlus.Brush brush = GdiPlus.Brush.Solid(argb);
        g.DrawString(text, font, brush, new GdiPlus.RectF(left, top, right - left, bottom - top), format);
    }

    private void Draw(DrawItem d)
    {
        Entry e = EntryFor((int)d.ItemData);
        User32.Rect r = d.Item;
        User32.FillRect(d.Dc, ref r, _surfaceBrush);
        if (e.Kind == Kind.Spacer) return;
        if (e.Kind == Kind.Separator)
        {
            int y = (r.Top + r.Bottom) / 2;
            var line = new User32.Rect { Left = r.Left + Scale(8), Top = y, Right = r.Right - Scale(8), Bottom = y + 1 };
            User32.FillRect(d.Dc, ref line, _separatorBrush);
            return;
        }
        bool enabled = e.Kind == Kind.Item;
        // The hovered row has its own text colours: in a contrast theme its fill is COLOR_HIGHLIGHT, which needs
        // COLOR_HIGHLIGHTTEXT.
        bool hot = enabled && (d.ItemState & OdsSelected) != 0;
        if (hot) _highlighted = (int)d.ItemData;
        if (hot)
        {
            IntPtr oldBrush = Gdi32.SelectObject(d.Dc, _hoverBrush);
            IntPtr oldPen = Gdi32.SelectObject(d.Dc, Gdi32.GetStockObject(NullPen));
            int radius = Scale(CornerRadius) * 2;
            Gdi32.RoundRect(d.Dc, r.Left + Scale(HoverInsetX), r.Top + Scale(HoverInsetY), r.Right - Scale(HoverInsetX), r.Bottom - Scale(HoverInsetY), radius, radius);
            Gdi32.SelectObject(d.Dc, oldPen);
            Gdi32.SelectObject(d.Dc, oldBrush);
        }
        // Normally built by BuildTheme; the ??= is only a fallback.
        Ink ink = _fonts ??= NewInk();
        using GdiPlus.Graphics g = Context(d.Dc);
        uint itemText = hot ? _ink.HotItem : _ink.Item;
        if (enabled && e.Checked?.Invoke() == true)
            Text(g, CheckGlyph, ink.Icon, ink.Centre, itemText, r.Left + Scale(CheckX), r.Top, r.Left + Scale(TextX), r.Bottom);
        if (enabled)
        {
            string? accel = e.Accelerator?.Invoke();
            if (!string.IsNullOrEmpty(accel))
                Text(g, accel, ink.Small, ink.Right, hot ? _ink.HotAccelerator : _ink.Accelerator, r.Left, r.Top, r.Right - Scale(RightPad), r.Bottom);
        }
        Text(g, e.Text, e.Kind == Kind.Header ? ink.Small : ink.Item, ink.Left,
             e.Kind == Kind.Header ? _ink.Header : enabled ? itemText : _ink.Disabled,
             r.Left + Scale(TextX), r.Top, r.Right - Scale(HoverInsetX), r.Bottom);
    }

    /// <summary>
    /// The widest row, which becomes the popup's width. Measured with GDI+ <c>MeasureString</c>, the renderer that
    /// actually draws the run, so nothing is selected into the shared screen DC.
    /// </summary>
    private void MeasureItems()
    {
        IntPtr screen = User32.GetDC(IntPtr.Zero);
        int widest = 0;
        try
        {
        Ink ink = _fonts ??= NewInk();
        using (GdiPlus.Graphics g = Context(screen))
            foreach (Entry e in _items)
            {
                if (e.Text.Length == 0) continue;
                (float w, float _) = g.MeasureString(e.Text, e.Kind == Kind.Item ? ink.Item : ink.Small, ink.Left);
                int rowWidth = Scale(TextX) + (int)Math.Ceiling(w);
                string? accel = e.Kind == Kind.Item ? e.Accelerator?.Invoke() : null;
                if (!string.IsNullOrEmpty(accel))
                {
                    (float aw, float _) = g.MeasureString(accel, ink.Small, ink.Left);
                    rowWidth += Scale(AccelGap) + (int)Math.Ceiling(aw);
                }
                widest = Math.Max(widest, rowWidth + Scale(RightPad));
            }
        }
        finally { User32.ReleaseDC(IntPtr.Zero, screen); }
        // The menu manager adds a check gutter to every owner-drawn item's reported width, so subtract it (from the
        // minimum too).
        int gutter = CheckGutter();
        _menuWidth = Math.Max(Scale(MenuMinWidth) - gutter, widest - gutter);
    }

    /// <summary>The width the menu manager adds to every owner-drawn item's rectangle, in this monitor's pixels.
    /// SM_CXMENUCHECK is at the system DPI, so it is normalised to 96 and rescaled.</summary>
    private int CheckGutter() => Scale(User32.GetSystemMetrics(SmCxMenuCheck) * 96 / Math.Max(96, (int)User32.GetDpiForSystem()));

    /// <summary>
    /// Every row drawn once, top to bottom, into a device context the caller owns, through the same code as the live
    /// menu. Used by the screenshot harness, which cannot capture the native popup; the menu manager's border, shadow
    /// and rounded corners are not included.
    /// </summary>
    /// <param name="dc">A device context to draw into, at least as large as the returned size.</param>
    /// <param name="dpi">The monitor DPI to scale for: 96, 120, 144.</param>
    /// <param name="highContrast">Which palette to build, instead of asking <see cref="IsHighContrast"/>.</param>
    /// <param name="hoveredId">The 1-based entry to draw hovered, as <c>WM_DRAWITEM</c> would report it; 0 for none.</param>
    /// <returns>The size actually drawn, so the caller can crop a generously sized surface down to it.</returns>
    public (int Width, int Height) DrawSheet(IntPtr dc, int dpi, bool highContrast, int hoveredId)
    {
        if (_open) throw new InvalidOperationException("TrayMenu.DrawSheet cannot run while the menu is up");
        _scaleNumerator = dpi > 0 ? dpi : 96;
        try
        {
            BuildTheme(highContrast);
            MeasureItems();
            // The manager hands an owner-drawn item a rectangle that includes the check gutter MeasureItems took off.
            int width = _menuWidth + CheckGutter();
            int y = 0;
            for (int id = 0; id <= _items.Count + 1; id++)
            {
                Entry e = EntryFor(id);
                int height = Scale(e.Kind switch { Kind.Separator => SeparatorHeight, Kind.Spacer => PadHeight, _ => RowHeight });
                Draw(new DrawItem
                {
                    CtlType = OdtMenu,
                    ItemState = id == hoveredId ? OdsSelected : 0,
                    Dc = dc,
                    Item = new User32.Rect { Left = 0, Top = y, Right = width, Bottom = y + height },
                    ItemData = (IntPtr)id,
                });
                y += height;
            }
            return (width, y);
        }
        finally { ReleaseTheme(); }
    }

    #region theme

    /// <summary>
    /// One owner-draw pass's colours, named by role. Every value is opaque 0xAARRGGBB: GDI has no alpha, so translucent
    /// tokens are flattened onto the surface first.
    /// </summary>
    /// <param name="Surface">The popup's own background, and the ground every other colour is chosen against.</param>
    /// <param name="Hover">Fill of the row under the pointer.</param>
    /// <param name="Separator">The hairline between groups.</param>
    /// <param name="Item">An enabled row's label and its check glyph.</param>
    /// <param name="HotItem">The same, on the hovered row.</param>
    /// <param name="Accelerator">The right-aligned chord on an enabled row.</param>
    /// <param name="HotAccelerator">The same, on the hovered row.</param>
    /// <param name="Header">A group heading, which is not interactive.</param>
    /// <param name="Disabled">A row that is interactive elsewhere but not now.</param>
    /// <param name="LightOnDark">Whether <paramref name="Surface"/> wants light text; drives the ClearType gamma.</param>
    private readonly record struct Palette(uint Surface, uint Hover, uint Separator, uint Item, uint HotItem,
                                           uint Accelerator, uint HotAccelerator, uint Header, uint Disabled, bool LightOnDark)
    {
        /// <summary>
        /// The same tokens as Theme/Dark.xaml and Theme/Light.xaml, flattened onto the surface. The hover fill is a
        /// translucent wash, not a filled selection, so the Hot* roles use the normal text colours.
        /// </summary>
        public static Palette App(bool dark)
        {
            uint surface = dark ? 0xFF202020u : 0xFFF3F3F3u;
            uint layer = dark ? 0x0FFFFFFFu : 0xB3FFFFFFu;
            uint stroke = dark ? 0x19FFFFFFu : 0x0F000000u;
            uint primary = Flatten(dark ? 0xFFFFFFFFu : 0xE4000000u, surface);
            uint secondary = Flatten(dark ? 0xC5FFFFFFu : 0x9E000000u, surface);
            uint disabled = Flatten(dark ? 0x5DFFFFFFu : 0x5C000000u, surface);
            return new Palette(surface, Flatten(layer, surface), Flatten(stroke, surface),
                               primary, primary, secondary, secondary, secondary, disabled, dark);
        }

        /// <summary>
        /// A contrast theme's palette, every entry straight from <c>GetSysColor</c> using the pairs Windows guarantees
        /// contrast for:
        /// <list type="bullet">
        /// <item>COLOR_MENU / COLOR_MENUTEXT: the popup and its text.</item>
        /// <item>COLOR_HIGHLIGHT / COLOR_HIGHLIGHTTEXT: the hovered row.</item>
        /// <item>COLOR_GRAYTEXT: headers, disabled rows and the separator. COLOR_3DFACE matches COLOR_MENU in the
        /// shipped contrast themes, so it would leave the separator invisible.</item>
        /// </list>
        /// <see cref="IsDark"/> is ignored. Headers stay smaller than items, so hierarchy is not carried by colour alone.
        /// </summary>
        public static Palette HighContrast()
        {
            uint surface = SystemTheme.SysColorArgb(SystemTheme.ColorMenu);
            uint text = SystemTheme.SysColorArgb(SystemTheme.ColorMenuText);
            uint grey = SystemTheme.SysColorArgb(SystemTheme.ColorGrayText);
            uint hotText = SystemTheme.SysColorArgb(SystemTheme.ColorHighlightText);
            return new Palette(surface, SystemTheme.SysColorArgb(SystemTheme.ColorHighlight), grey,
                               // Accelerators belong to enabled rows, so they use the full text colour, not grey.
                               text, hotText, text, hotText, grey, grey, SystemTheme.IsDarkColour(surface));
        }
    }

    private void BuildTheme(bool highContrast)
    {
        _ink = highContrast ? Palette.HighContrast() : Palette.App(IsDark());
        _fonts?.Dispose();
        _fonts = NewInk();          // one set for the whole pass, at this pass's DPI
        _surfaceBrush = Gdi32.CreateSolidBrush(Ref(_ink.Surface));
        _hoverBrush = Gdi32.CreateSolidBrush(Ref(_ink.Hover));
        _separatorBrush = Gdi32.CreateSolidBrush(Ref(_ink.Separator));
    }

    private void ReleaseTheme()
    {
        foreach (IntPtr h in new[] { _surfaceBrush, _hoverBrush, _separatorBrush })
            if (h != IntPtr.Zero) Gdi32.DeleteObject(h);
        _surfaceBrush = _hoverBrush = _separatorBrush = IntPtr.Zero;
        _fonts?.Dispose();
        _fonts = null;
    }

    /// <summary>0xAARRGGBB composited over an opaque background, still 0xAARRGGBB.</summary>
    private static uint Flatten(uint argb, uint background)
    {
        uint a = argb >> 24;
        if (a == 255) return argb;
        uint Mix(int shift) => (((argb >> shift) & 0xFF) * a + ((background >> shift) & 0xFF) * (255 - a)) / 255;
        return 0xFF000000u | (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
    }

    /// <summary>0xAARRGGBB to a GDI COLORREF (0x00BBGGRR).</summary>
    private static uint Ref(uint argb) => ((argb & 0xFF) << 16) | (argb & 0xFF00) | ((argb >> 16) & 0xFF);

    private static int DpiAt(int x, int y)
    {
        try
        {
            IntPtr monitor = User32.MonitorFromPoint(((long)(uint)y << 32) | (uint)x, MonitorDefaultToNearest);
            if (Shcore.GetDpiForMonitor(monitor, 0 /*MDT_EFFECTIVE_DPI*/, out uint dpiX, out _) == 0 && dpiX > 0) return (int)dpiX;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 96;
    }

    #endregion
}
