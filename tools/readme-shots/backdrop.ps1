param([string]$Color = '#202020', [int]$Seconds = 900)
# A plain window over the whole virtual screen, behind the windows the UI suite opens, so a screen capture of them
# shows this colour instead of whatever was on the desktop. It never takes the foreground and is not topmost, so
# ToneSnip's windows still open above it and the suite's foreground guard is untouched.
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace TS {
public class Backdrop : Form {
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    public Backdrop(Color c) {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        BackColor = c;
        ShowInTaskbar = false;
        Text = "tonesnip-readme-backdrop";
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
    protected override CreateParams CreateParams {
        get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080; return cp; }
    }
    public static void Run(string color, int seconds) {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        Backdrop f = new Backdrop(ColorTranslator.FromHtml(color));
        Timer close = new Timer { Interval = seconds * 1000 };
        close.Tick += (s, e) => f.Close();
        close.Start();
        Timer show = new Timer { Interval = 300 };
        show.Tick += (s, e) => { show.Stop(); f.Bounds = SystemInformation.VirtualScreen; ShowWindow(f.Handle, 4); };
        show.Start();
        Application.Run(f);
    }
}
}
"@
[TS.Backdrop]::Run($Color, $Seconds)
