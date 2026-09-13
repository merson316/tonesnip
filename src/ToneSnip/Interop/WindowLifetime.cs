using Microsoft.UI.Xaml;

namespace ToneSnip.App.Interop;

/// <summary>
/// Subscribes to <see cref="Window.Closed"/> and <see cref="Window.Activated"/> so the window can still be collected
/// after it closes.
/// </summary>
/// <remarks>
/// A handler on either event that references the window keeps it alive: the native window holds the handler's COM
/// wrapper, which holds the delegate, which holds the managed window. The cycle crosses the native boundary, so the GC
/// cannot collect it. Handlers on elements inside the window, or on its <c>AppWindow</c>, are not affected. The fix is
/// to unsubscribe when the window closes, which these helpers do.
/// </remarks>
public static class WindowLifetime
{
    /// <summary>Runs <paramref name="body"/> when the window closes, and detaches itself first, so the handler cannot
    /// keep the closed window alive.</summary>
    public static void WhenClosed(this Window window, Action body)
    {
        void Handler(object sender, WindowEventArgs e)
        {
            window.Closed -= Handler;
            body();
        }
        window.Closed += Handler;
    }

    /// <summary>Runs <paramref name="body"/> on every activation change for as long as the window is open; the
    /// subscription is dropped when it closes.</summary>
    public static void WhenActivated(this Window window, Action<WindowActivatedEventArgs> body)
    {
        void Handler(object sender, WindowActivatedEventArgs e) => body(e);
        window.Activated += Handler;
        window.WhenClosed(() => window.Activated -= Handler);
    }
}
