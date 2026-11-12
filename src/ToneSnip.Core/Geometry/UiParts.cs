namespace ToneSnip.Core.Geometry;

/// <summary>One element of a window's UI Automation tree: where it is on screen, its control type
/// (UIA_*ControlTypeId) and whether it says it is scrolled or clipped out of view.</summary>
public readonly record struct UiElement(IntRect Bounds, int ControlType, bool Offscreen);

/// <summary>
/// Which UI Automation elements window mode offers as parts of a window that has no child windows: the areas an app
/// is laid out in (a page, a pane, a list, a toolbar), not the controls inside them. A button or a line of text is a
/// rectangle snip's job, and offering each would make the highlight jump with every pointer move.
/// </summary>
public static class UiParts
{
    /// <summary>The shortest side, in pixels, a part may have: below it the element is a control, whatever it says.</summary>
    public const int MinSide = 48;

    // UIA_*ControlTypeId, from UIAutomationClient.h.
    public const int Edit = 50004, Image = 50006, List = 50008, MenuBar = 50010, StatusBar = 50017, Tab = 50018,
                     ToolBar = 50021, Tree = 50023, Custom = 50025, Group = 50026, DataGrid = 50028, Document = 50030,
                     Window = 50032, Pane = 50033, Table = 50036, TitleBar = 50037, SemanticZoom = 50039, AppBar = 50040;

    /// <summary>True when the element is an area worth a step: on screen, inside <paramref name="frame"/>, at least
    /// <see cref="MinSide"/> on each side, and of a type that holds content rather than being one control.</summary>
    public static bool IsPart(UiElement e, IntRect frame)
    {
        if (e.Offscreen) return false;
        IntRect seen = e.Bounds.Intersect(frame);
        if (seen.Width < MinSide || seen.Height < MinSide) return false;
        return e.ControlType is Edit or Image or List or MenuBar or StatusBar or Tab or ToolBar or Tree or Custom or Group
            or DataGrid or Document or Window or Pane or Table or TitleBar or SemanticZoom or AppBar;
    }

    /// <summary>True when the element's children are worth reading: it is a part, and of a type whose children are
    /// areas too. A toolbar, a menu bar, a title bar, a picture and a text box hold only controls or nothing.</summary>
    public static bool HasPartsInside(UiElement e, IntRect frame)
        => IsPart(e, frame) && e.ControlType is not (Edit or Image or MenuBar or StatusBar or ToolBar or TitleBar);
}
