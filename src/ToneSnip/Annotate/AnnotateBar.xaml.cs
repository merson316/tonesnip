using ToneSnip.App.Theme;
using ToneSnip.Core.Annotate;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
// `Style` is also FrameworkElement.Style, which wins in expression position, so the static arrays below are reached
// through Core.Annotate.Style explicitly.
using Style = ToneSnip.Core.Annotate.Style;
using XamlStyle = Microsoft.UI.Xaml.Style;

namespace ToneSnip.App.Annotate;

/// <summary>The tool row shared by the overlay toolbar and the viewer. Talks to an <see cref="EditSession"/>; hosts handle exposure, zebra and done.</summary>
public sealed partial class AnnotateBar : UserControl
{
    private const int VkReturn = 0x0D, VkDelete = 0x2E, VkB = 0x42, VkC = 0x43, VkE = 0x45, VkH = 0x48, VkI = 0x49,
                      VkN = 0x4E, VkO = 0x4F, VkP = 0x50, VkR = 0x52, VkT = 0x54, VkV = 0x56, VkX = 0x58, VkY = 0x59, VkZ = 0x5A;

    /// <summary>The row's padding, which is also where the band's content starts: a panel aligned "under its button"
    /// carries the button's offset within the row less this inset.</summary>
    private const double RowInset = 4;
    private const double BandFadeMs = 120;

    private EditSession? _session;
    private uint _accent = ShapeRenderer.DefaultAccent;
    /// <summary>Set while <see cref="Refresh"/> writes the controls, so <see cref="OnExposureValue"/> ignores the
    /// ValueChanged that setting the slider raises. Everything else in the row reports through Click.</summary>
    private bool _syncing;
    private Storyboard? _bandFade;
    private readonly Action _onToolChanged;
    private readonly Action<Core.Geometry.IntRect> _onChanged;
    /// <summary>Palette cells, rebuilt per Attach. The ring, gap and (white and black only) outline brushes come from
    /// the Tok swatches and are re-assigned on a theme change.</summary>
    private readonly List<(uint Colour, Ellipse Ring, Ellipse Gap, Ellipse? Outline, Button Cell, string Name)> _swatchCells = new();
    /// <summary>The picked swatch's name, for the Colour picker's own name; null when the colour is none of them.</summary>
    private string? _pickedColour;
    /// <summary>A refresh of the undo buttons is already queued. EditSession.Changed fires on every pointer move of a
    /// stroke, so one queued refresh serves every move until it runs.</summary>
    private bool _undoQueued;
    private readonly List<(int Value, RadioButton Cell, Rectangle Bar)> _widthCells = new();
    private readonly List<SizeCell> _sizeCells = new();
    private bool _sizeCounterBuilt;

    /// <summary>Accessible names for <see cref="Core.Annotate.Style.Palette"/>, in its order, since a swatch's only
    /// content is a coloured <see cref="Ellipse"/>. <c>StyleTests.Palette_length_is_pinned</c> fails if the palette
    /// changes length.</summary>
    private static readonly string[] SwatchNames = { "Red", "Amber", "Yellow", "Green", "Blue", "Purple", "White", "Black", "Accent" };

    /// <summary>Outline thickness for the white and black swatches. The tool row's preview Ellipse in AnnotateBar.xaml
    /// repeats this as a literal (setting it from code breaks XAML loading); change them together.</summary>
    private const double SwatchEdgePx = 1;

    public AnnotateBar()
    {
        InitializeComponent();
        _onToolChanged = Refresh;
        _onChanged = _ =>
        {
            if (_undoQueued) return;
            _undoQueued = true;
            DispatcherQueue.TryEnqueue(() => { _undoQueued = false; RefreshUndo(); });
        };
        SyncPickerNames();   // every band starts shut
        // No unhook needed: a self-subscription keeps nothing else alive.
        ActualThemeChanged += OnThemeChanged;
    }

    /// <summary>Re-reads every brush copied from a Tok swatch in code, which goes stale on a theme change.
    /// <c>ActualThemeChanged</c> fires after the framework has re-resolved the swatches' ThemeResources;
    /// <c>ThemeManager.Changed</c> fires before. The FontIcons inherit Foreground and need nothing.</summary>
    private void OnThemeChanged(FrameworkElement sender, object args)
    {
        foreach ((uint _, Ellipse ring, Ellipse gap, Ellipse? outline, Button _, string _) in _swatchCells)
        {
            ring.Stroke = TokPrimary.Background;
            gap.Fill = TokSurface.Background;
            if (outline != null) outline.Stroke = TokEdge.Background;
        }
        SyncIcons();
        // With no session, a null style re-reads the cells' brushes without touching which one is picked.
        SyncBands(_session?.Style);
    }

    /// <summary>True while one of the pickers is expanded under the row.</summary>
    public bool AnyPopupOpen => BandHost.Visibility == Visibility.Visible;

    /// <summary>Closes the colour, width, size and exposure pickers; hosts call it when the pointer goes elsewhere.</summary>
    // Nothing here handles Escape: the overlay session owns it, and the viewer calls this from its own Escape path.
    public void ClosePopups() { if (ClosePanels()) Interacted?.Invoke(); }

    public event Action<float>? ExposureChanged;
    public event Action<bool>? ZebraChanged;
    public event Action? DoneClicked;
    public event Action? Interacted;
    public event Action<Style>? StyleChanged;
    /// <summary>A picker is about to expand under the row; a host with pickers of its own closes those.</summary>
    public event Action? PanelOpened;

    /// <param name="hdrTooltip">Why exposure and zebra are off when <paramref name="hdr"/> is false; the reason differs
    /// per host (no HDR monitor in the snip, versus a flow that did not keep the float data).</param>
    /// <param name="showHdrControls">False drops exposure and zebra from the row: the editor carries them in its own
    /// command bar, while the overlay, whose window never activates, keeps them here.</param>
    public void Attach(EditSession session, bool hdr, bool showCrop, bool showDone, uint accentArgb, string? hdrTooltip = null, bool showHdrControls = true)
    {
        if (_session != null) { _session.ToolChanged -= _onToolChanged; _session.Changed -= _onChanged; }
        _session = session; _accent = accentArgb;
        session.ToolChanged += _onToolChanged;
        session.Changed += _onChanged;
        TCrop.Visibility = showCrop ? Visibility.Visible : Visibility.Collapsed;
        ExposureBtn.IsEnabled = ZebraBtn.IsEnabled = hdr;
        if (!hdr)
        {
            string why = hdrTooltip ?? "HDR data is kept only in the save-then-edit flow";
            ToolTipService.SetToolTip(ExposureHost, why);
            ToolTipService.SetToolTip(ZebraHost, why);
        }
        Visibility hdrControls = showHdrControls ? Visibility.Visible : Visibility.Collapsed;
        HdrDivider.Visibility = ExposureHost.Visibility = ZebraHost.Visibility = hdrControls;
        DoneDivider.Visibility = DoneBtn.Visibility = showDone ? Visibility.Visible : Visibility.Collapsed;

        _swatchCells.Clear();
        PaletteRow.Children.Clear();
        for (int i = 0; i < Core.Annotate.Style.Palette.Length; i++)
        {
            uint c = Core.Annotate.Style.Palette[i];
            // The accent swatch follows the system accent rather than a fixed colour, so it sits after a divider.
            if (i == Core.Annotate.Style.Palette.Length - 1) PaletteRow.Children.Add(Divider());
            PaletteRow.Children.Add(SwatchCell(c, i, Core.Annotate.Style.Palette.Length));
        }
        _widthCells.Clear();
        WidthList.Children.Clear();
        for (int i = 0; i < Core.Annotate.Style.Widths.Length; i++)
        {
            int w = Core.Annotate.Style.Widths[i];
            var bar = new Rectangle { Width = 16, Height = w, RadiusX = w / 2.0, RadiusY = w / 2.0, VerticalAlignment = VerticalAlignment.Center };
            RadioButton cell = BandCell(bar, $"{w} px", $"Tools_Width{w}", i, Core.Annotate.Style.Widths.Length,
                                        () => { if (_session is { } s) SetStyle(s.Style with { Width = w }); });
            _widthCells.Add((w, cell, bar));
            WidthList.Children.Add(cell);
        }
        _sizeCells.Clear();
        SizeList.Children.Clear();
        for (int i = 0; i < Core.Annotate.Style.TextSizes.Length; i++)
        {
            int sz = Core.Annotate.Style.TextSizes[i];
            // Both glyphs live in every cell and only one shows, since the size picker sets text and counter size alike.
            double px = GlyphPx(sz);
            var letter = new TextBlock { Style = (XamlStyle)Resources["CellGlyph"], Text = "A", FontSize = px };
            FontIcon counter = CounterGlyph(px);
            counter.Visibility = Visibility.Collapsed;
            var box = new Grid();   // no fixed size: the 32 px cell centres whichever glyph is showing
            box.Children.Add(letter); box.Children.Add(counter);
            RadioButton cell = BandCell(box, $"{sz} px", $"Tools_TextSize{sz}", i, Core.Annotate.Style.TextSizes.Length,
                                        () => { if (_session is { } s) SetStyle(s.Style with { TextSize = sz }); });
            _sizeCells.Add(new SizeCell(sz, cell, letter, counter));
            SizeList.Children.Add(cell);
        }
        if (!_sizeCounterBuilt)
        {
            _sizeCounterBuilt = true;
            SizeCounterHost.Children.Add(CounterGlyph(16));
        }
        Refresh();
    }

    /// <summary>Drops the EditSession subscriptions. Hosts call it when the bar goes away: the document outlives the
    /// snip and would otherwise keep the bar and its window alive.</summary>
    public void Detach()
    {
        if (_session == null) return;
        _session.ToolChanged -= _onToolChanged;
        _session.Changed -= _onChanged;
        _session = null;
    }

    /// <summary>One colour of the palette band: a 20 px swatch in a 32 px cell. The selection ring is always in the tree
    /// and only toggled, so picking never shifts the layout.</summary>
    private Button SwatchCell(uint colour, int index, int count)
    {
        uint resolved = ShapeRenderer.ResolveColor(colour, _accent);
        var ring = new Ellipse
        {
            Width = 26, Height = 26, Stroke = TokPrimary.Background, StrokeThickness = 1.5, Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var gap = new Ellipse
        {
            Width = 22, Height = 22, Fill = TokSurface.Background, Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var swatch = new Ellipse
        {
            Width = 20, Height = 20, Fill = Brush(resolved),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        // White and black would vanish into the surface, so they alone get an outline, in ControlEdge (Stroke is too
        // faint). A thicker stroke barely helps contrast: Stretch=Fill insets an Ellipse's geometry by half the pen
        // width, so the outline's outer edge gains almost no coverage. The white swatch on the light theme sits just
        // under 3:1.
        bool outlined = colour is 0xFFFFFFFFu or 0xFF000000u;
        if (outlined) { swatch.Stroke = TokEdge.Background; swatch.StrokeThickness = SwatchEdgePx; }
        var box = new Grid { Width = 32, Height = 32 };
        box.Children.Add(ring); box.Children.Add(gap); box.Children.Add(swatch);
        var cell = new Button { Style = (XamlStyle)Resources["SwatchCell"], Content = box };
        // A Button rather than a radio: the picked state is the ring (and the name), and the accent swatch is a
        // different kind of choice from the fixed colours.
        string name = index >= 0 && index < SwatchNames.Length ? SwatchNames[index] : $"Colour {index + 1}";
        AutomationProperties.SetName(cell, name);
        AutomationProperties.SetAutomationId(cell, $"Tools_Swatch{name}");
        ToolTipService.SetToolTip(cell, name);
        // "Blue, 5 of 9": a Button in a bare panel has no set semantics of its own. The divider is Raw, so it does not
        // count.
        AutomationProperties.SetPositionInSet(cell, index + 1);
        AutomationProperties.SetSizeOfSet(cell, count);
        // Detach() may have run before the click is delivered; SetStyle's null check comes too late for the argument.
        cell.Click += (_, _) => { if (_session is { } s) { SetStyle(s.Style with { Color = colour }); ClosePanels(); } };
        _swatchCells.Add((colour, ring, gap, outlined ? swatch : null, cell, name));
        return cell;
    }

    /// <summary>One cell of the width or size band: a radio button around a to-scale sample, grouped by its panel. The
    /// sample has no text, so the label is both tooltip and accessible name.</summary>
    private RadioButton BandCell(FrameworkElement sample, string label, string automationId, int index, int count, Action pick)
    {
        var cell = new RadioButton { Style = (XamlStyle)Resources["BandCell"], Content = sample };
        ToolTipService.SetToolTip(cell, label);
        AutomationProperties.SetName(cell, label);
        AutomationProperties.SetAutomationId(cell, automationId);
        AutomationProperties.SetPositionInSet(cell, index + 1);
        AutomationProperties.SetSizeOfSet(cell, count);
        cell.Click += (_, _) => { pick(); ClosePanels(); };
        return cell;
    }

    /// <summary>One size cell: both glyph forms, only one of them showing.</summary>
    private sealed record SizeCell(int Value, RadioButton Cell, TextBlock Letter, FrameworkElement Counter)
    {
        public void ShowCounter(bool counter)
        {
            Letter.Visibility = counter ? Visibility.Collapsed : Visibility.Visible;
            Counter.Visibility = counter ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>How big a size cell draws its sample: small / medium / large by position in
    /// <see cref="Core.Annotate.Style.TextSizes"/>, not the text size itself.</summary>
    private static double GlyphPx(int textSize)
    {
        int i = Array.IndexOf(Core.Annotate.Style.TextSizes, textSize);
        double[] steps = { 11, 15, 20 };
        return i < 0 ? 15 : steps[Math.Min(i, steps.Length - 1)];
    }

    /// <summary>The counter tool's glyph at a given size, copied from the counter tool's own icon.</summary>
    private FontIcon CounterGlyph(double px) => new()
    {
        Glyph = CounterIcon.Glyph,
        FontFamily = CounterIcon.FontFamily,
        FontSize = px,
    };

    /// <summary>The hairline before the accent swatch. Raw, so it is not counted in the swatches' PositionInSet.</summary>
    private Rectangle Divider()
    {
        var r = new Rectangle { Style = (XamlStyle)Resources["BandDivider"] };
        AutomationProperties.SetAccessibilityView(r, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return r;
    }

    /// <summary>Shows or hides the Done button after <see cref="Attach"/>; the overlay only earns one once a selection exists.</summary>
    public void SetDone(bool show) => DoneDivider.Visibility = DoneBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Disables the exposure button while leaving it visible. The tooltip goes on its wrapper, since a disabled
    /// button shows no tooltip.</summary>
    public void SetExposureEnabled(bool enabled, string? tooltip = null)
    {
        ExposureBtn.Visibility = Visibility.Visible;
        ExposureBtn.IsEnabled = enabled;
        if (!enabled)
        {
            if (ExposurePanel.Visibility == Visibility.Visible) ClosePanels();
            ExposureBtn.IsChecked = false;
        }
        if (tooltip != null) ToolTipService.SetToolTip(ExposureHost, tooltip);
        SyncIcons();
    }

    public void Refresh()
    {
        if (_session == null) return;
        _syncing = true;
        // The tools are the row's RadioButtons with a string Tag; the pickers are ToggleButtons.
        foreach (RadioButton t in Row.Children.OfType<RadioButton>()) if (t.Tag is string name) t.IsChecked = name == _session.Tool.ToString();
        CropApply.Visibility = _session.Tool == Tool.Crop ? Visibility.Visible : Visibility.Collapsed;
        Style s = _session.Style;
        Swatch.Fill = Brush(ShapeRenderer.ResolveColor(s.Color, _accent));
        WidthBar.Height = s.Width;
        WidthBar.RadiusX = WidthBar.RadiusY = s.Width / 2.0;
        ExposureText.Text = _session.Doc.Exposure == 1f ? "0 EV" : $"{Math.Log2(_session.Doc.Exposure):+0.00;-0.00} EV";
        double ev = Math.Log2(_session.Doc.Exposure);
        if (Math.Abs(ExposureSlider.Value - ev) > 1e-6) ExposureSlider.Value = ev;   // an ulp round-trip would re-raise ExposureChanged
        ZebraBtn.IsChecked = _session.Doc.Zebra;
        RefreshUndo();
        SyncSizeKind();
        SyncBands(s);
        SyncIcons();
        _syncing = false;
    }

    private void RefreshUndo() { if (_session == null) return; UndoBtn.IsEnabled = _session.Doc.CanUndo; RedoBtn.IsEnabled = _session.Doc.CanRedo; }

    /// <summary>Marks the picked colour, width and size in the bands, and re-reads the drawn samples' brushes.</summary>
    /// <param name="s">The session's style, or null when the bar is detached; then only the brushes are re-read.</param>
    private void SyncBands(Style? s)
    {
        if (s is { } style)
        {
            _pickedColour = null;
            foreach ((uint colour, Ellipse ring, Ellipse gap, Ellipse? _, Button cell, string name) in _swatchCells)
            {
                bool picked = colour == style.Color;
                ring.Visibility = gap.Visibility = picked ? Visibility.Visible : Visibility.Collapsed;
                // The ring is invisible to a screen reader and the swatches are Buttons, so the state goes into the name.
                AutomationProperties.SetName(cell, picked ? $"{name}, selected" : name);
                if (picked) _pickedColour = name;
            }
            SyncPickerNames();
        }
        foreach ((int value, RadioButton cell, Rectangle bar) in _widthCells)
        {
            if (s is { } width) cell.IsChecked = value == width.Width;
            bar.Fill = cell.IsChecked == true ? TokOnAccent.Background : TokPrimary.Background;
        }
        foreach (SizeCell c in _sizeCells)
        {
            if (s is { } text) c.Cell.IsChecked = c.Value == text.TextSize;
            c.Letter.Foreground = c.Cell.IsChecked == true ? TokOnAccent.Background : TokPrimary.Background;
        }
    }

    /// <summary>Which sample the size picker shows: the counter's circled "1" while the counter tool is live or a
    /// counter is selected (EditSession restyles the selection from the picked size), the "A" otherwise.</summary>
    private void SyncSizeKind()
    {
        bool counter = _session is { } s && (s.Tool == Tool.Counter || s.Doc.Selected is CounterShape);
        SizeGlyph.Visibility = counter ? Visibility.Collapsed : Visibility.Visible;
        SizeCounterHost.Visibility = counter ? Visibility.Visible : Visibility.Collapsed;
        foreach (SizeCell c in _sizeCells) c.ShowCounter(counter);
    }

    /// <summary>The width bar and size letter are not icons and do not inherit Foreground, so their brushes follow the
    /// button's enabled state from code.</summary>
    private void SyncIcons()
    {
        WidthBar.Fill = GlyphBrush(WidthBtn);
        SizeGlyph.Foreground = GlyphBrush(SizeBtn);
    }

    private Brush GlyphBrush(Control c) => c.IsEnabled ? TokPrimary.Background : TokDisabled.Background;

    private void SetStyle(Style s) { if (_session == null) return; _session.Style = s; StyleChanged?.Invoke(s); Refresh(); Interacted?.Invoke(); }

    private void OnTool(object sender, RoutedEventArgs e)
    {
        ClosePanels();
        if (_session == null || sender is not RadioButton t || t.Tag is not string name) return;
        _session.Tool = Enum.Parse<Tool>(name);
        Refresh(); Interacted?.Invoke();
    }

    /// <summary>Collapses whichever picker is open. True when one was.</summary>
    private bool ClosePanels()
    {
        if (BandHost.Visibility == Visibility.Collapsed) return false;
        BandHost.Visibility = Visibility.Collapsed;
        HidePanels();
        SyncIcons();
        SyncPickerNames();
        return true;
    }

    private void HidePanels()
    {
        PaletteRow.Visibility = WidthList.Visibility = SizeList.Visibility = ExposurePanel.Visibility = Visibility.Collapsed;
        SwatchBtn.IsChecked = WidthBtn.IsChecked = SizeBtn.IsChecked = ExposureBtn.IsChecked = false;
    }

    /// <summary>Appends whether each picker's band is open to its accessible name, since WinUI has no attachable
    /// ExpandCollapseState. The base word comes from the tooltip.</summary>
    private void SyncPickerNames()
    {
        foreach (ToggleButton picker in new[] { SwatchBtn, WidthBtn, SizeBtn, ExposureBtn })
        {
            if (ToolTipService.GetToolTip(picker) is not string word) continue;
            // "Colour, Blue, collapsed": the swatch on the button is drawn, so the colour is spoken.
            string label = ReferenceEquals(picker, SwatchBtn) && _pickedColour != null ? $"{word}, {_pickedColour}" : word;
            AutomationProperties.SetName(picker, picker.IsChecked == true ? $"{label}, expanded" : $"{label}, collapsed");
        }
    }

    /// <summary>Expands one picker in the band under the row, or closes it when its button was toggled off. Opening a
    /// second picker swaps the panel in place.</summary>
    private void ShowPanel(FrameworkElement panel, ToggleButton owner)
    {
        PanelOpened?.Invoke();          // one picker at a time, the host's own included
        bool open = owner.IsChecked == true;
        HidePanels();
        owner.IsChecked = open;
        if (open)
        {
            // Visible before it is placed: AlignUnder needs the panel's measured width, and a collapsed element has none.
            BandHost.Visibility = panel.Visibility = Visibility.Visible;
            AlignUnder(panel, owner);
            FadeInBand();
        }
        else BandHost.Visibility = Visibility.Collapsed;
        SyncIcons();
        SyncPickerNames();
        if (!open) Interacted?.Invoke();
    }

    /// <summary>Puts a panel's first cell under the button that opened it, then pulls it back until its right edge is
    /// inside the row, so a wide band opened from a right-hand button does not widen the window.</summary>
    private void AlignUnder(FrameworkElement panel, FrameworkElement button)
    {
        double x = 0;
        try { x = button.TransformToVisual(Row).TransformPoint(new global::Windows.Foundation.Point(0, 0)).X - RowInset; }
        catch { x = 0; }
        if (double.IsNaN(x) || double.IsInfinity(x)) x = 0;
        double wanted = x, width = PanelWidth(panel);
        // The band host is inset by RowInset on each side, so a panel at margin x spans [RowInset + x, RowInset + x +
        // width) in row coordinates, and the row's last button ends at ActualWidth - RowInset.
        double room = Row.ActualWidth - 2 * RowInset - width;
        if (Row.ActualWidth > 0 && !double.IsNaN(room) && x > room) x = room;
        if (x < 0) x = 0;
        panel.Margin = new Thickness(x, 0, 0, 0);
#if TONESNIP_HARNESS
        _placement = (Row.ActualWidth, width, wanted, x);
#endif
    }

#if TONESNIP_HARNESS
    /// <summary>The last band placement, for <see cref="OpenPanel"/> to log: row width, panel width, and the offset
    /// before and after the clamp.</summary>
    private (double Row, double Width, double Wanted, double Got) _placement;
#endif

    /// <summary>A panel's natural width: ActualWidth once arranged, otherwise an unbounded measure.</summary>
    private static double PanelWidth(FrameworkElement panel)
    {
        if (panel.ActualWidth > 0) return panel.ActualWidth;
        panel.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return panel.DesiredSize.Width;
    }

    /// <summary>Fades the band's content in. The height is not animated because the hosting window is sized to its
    /// content.</summary>
    private void FadeInBand()
    {
        // One fade at a time: stop the last one rather than let it race the new one.
        _bandFade?.Stop();
        _bandFade = null;
        BandContent.Opacity = 1;
        if (!ThemeManager.AnimationsEnabled) return;
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(BandFadeMs)) };
        Storyboard.SetTarget(fade, BandContent);
        Storyboard.SetTargetProperty(fade, "Opacity");
        _bandFade = new Storyboard();
        _bandFade.Children.Add(fade);
        _bandFade.Begin();
    }

#if TONESNIP_HARNESS
    /// <summary>Screenshot harness: expands one picker band without a click ("colour", "width", "size", "exposure").</summary>
    internal void OpenPanel(string name)
    {
        (FrameworkElement panel, ToggleButton owner) = name switch
        {
            "width" => ((FrameworkElement)WidthList, WidthBtn),
            "size" => (SizeList, SizeBtn),
            "exposure" => (ExposurePanel, ExposureBtn),
            _ => (PaletteRow, SwatchBtn),
        };
        owner.IsChecked = true;
        ShowPanel(panel, owner);
        // Logged because a screenshot alone cannot show whether the band was clamped.
        (double row, double width, double wanted, double got) = _placement;
        App.Current.Log.Debug($"band \"{name}\": row {row:0}, panel {width:0}, left {got:0} (wanted {wanted:0}), " +
                              $"right edge {RowInset + got + width:0} of {row - RowInset:0}");
    }
#endif

    private void OnApplyCrop(object sender, RoutedEventArgs e) { ClosePanels(); _session?.ApplyCrop(); Refresh(); Interacted?.Invoke(); }
    private void OnSwatch(object sender, RoutedEventArgs e) => ShowPanel(PaletteRow, SwatchBtn);
    private void OnWidth(object sender, RoutedEventArgs e) => ShowPanel(WidthList, WidthBtn);
    private void OnTextSize(object sender, RoutedEventArgs e) => ShowPanel(SizeList, SizeBtn);
    private void OnExposure(object sender, RoutedEventArgs e) => ShowPanel(ExposurePanel, ExposureBtn);
    private void OnUndo(object sender, RoutedEventArgs e) { ClosePanels(); _session?.Doc.Undo(); Refresh(); Interacted?.Invoke(); }
    private void OnRedo(object sender, RoutedEventArgs e) { ClosePanels(); _session?.Doc.Redo(); Refresh(); Interacted?.Invoke(); }

    private void OnExposureValue(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || _session == null) return;
        float ev = (float)Math.Pow(2, ExposureSlider.Value);
        _session.Doc.Exposure = ev;
        ExposureText.Text = ExposureSlider.Value == 0 ? "0 EV" : $"{ExposureSlider.Value:+0.00;-0.00} EV";
        ExposureChanged?.Invoke(ev);
    }

    private void OnZebra(object sender, RoutedEventArgs e)
    {
        ClosePanels();
        if (_session == null) return;
        _session.Doc.Zebra = ZebraBtn.IsChecked == true;
        ZebraChanged?.Invoke(_session.Doc.Zebra);
        Interacted?.Invoke();
    }

    private void OnDone(object sender, RoutedEventArgs e) { ClosePanels(); DoneClicked?.Invoke(); }

    private static SolidColorBrush Brush(uint argb)
        => new(new global::Windows.UI.Color { A = (byte)(argb >> 24), R = (byte)(argb >> 16), G = (byte)(argb >> 8), B = (byte)argb });

    /// <summary>What a key did, so the overlay can tell picking a tool (which takes the mouse back from the capture
    /// modes) from a command like undo or delete.</summary>
    public enum KeyHandled { No, Tool, Command }

    /// <summary>Keyboard shortcuts shared by both hosts, as virtual-key codes: the overlay reads Win32 keys and the
    /// viewer casts its <c>VirtualKey</c>.</summary>
    public KeyHandled HandleKey(int vk, bool ctrl)
    {
        if (_session == null) return KeyHandled.No;
        Tool? t = (vk, ctrl) switch
        {
            (VkV, false) => Tool.Select, (VkP, false) => Tool.Pen, (VkH, false) => Tool.Highlighter,
            (VkI, false) => Tool.Line, (VkO, false) => Tool.Arrow, (VkR, true) => Tool.Rect,
            (VkE, false) => Tool.Ellipse, (VkT, false) => Tool.Text, (VkN, false) => Tool.Counter,
            (VkB, false) => Tool.Blur, (VkX, false) => Tool.Pixelate,
            (VkC, false) when TCrop.Visibility == Visibility.Visible => Tool.Crop,
            _ => null,
        };
        if (t is Tool tool) { _session.Tool = tool; Refresh(); return KeyHandled.Tool; }
        if (ctrl && vk == VkZ) { _session.Doc.Undo(); Refresh(); return KeyHandled.Command; }
        if (ctrl && vk == VkY) { _session.Doc.Redo(); Refresh(); return KeyHandled.Command; }
        if (vk == VkDelete) return _session.DeleteSelected() ? KeyHandled.Command : KeyHandled.No;
        if (vk == VkReturn && !_session.CropMarquee.IsEmpty) { _session.ApplyCrop(); Refresh(); return KeyHandled.Command; }
        return KeyHandled.No;
    }
}
