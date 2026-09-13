namespace ToneSnip.Core.Annotate;

public enum Tool { Select, Pen, Highlighter, Line, Arrow, Rect, Ellipse, Text, Counter, Blur, Pixelate, Crop }

[Flags] public enum InputMods { None = 0, Shift = 1, Ctrl = 2 }
