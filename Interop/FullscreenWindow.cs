using System.Runtime.InteropServices;

namespace DevShell.Interop;

/// <summary>
/// Answers the one question the bar has to get out of the way for: is the window in front a
/// fullscreen application. Windows decides this for its own auto-hiding taskbar the same way - by
/// measuring the foreground window against the monitor it sits on - and a maximized window is
/// deliberately not a fullscreen one, because it stops at the work area and so leaves the bar its
/// strip at the bottom.
/// </summary>
internal static class FullscreenWindow
{
    /// <summary>A window can sit a pixel or two inside its monitor, so the fit is generous by two.</summary>
    private const int Slack = 2;

    /// <summary>True when the window the user is looking at belongs to another program and fills its monitor.</summary>
    public static bool IsForegroundFullscreen()
        => IsFullscreen(NativeMethods.GetForegroundWindow());

    /// <summary>
    /// True when <paramref name="window"/> is a visible, on-screen, borderless window covering the
    /// monitor it is on. DevShell's own windows never count: the bar, its flyouts, its dialogs and the
    /// desktop layer are the shell rather than the application asking for the whole screen.
    /// </summary>
    public static bool IsFullscreen(IntPtr window)
    {
        // The desktop is always exactly as big as the monitor and is never what fullscreen means.
        if (window == IntPtr.Zero || window == NativeMethods.GetShellWindow())
        {
            return false;
        }

        if (!NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return false;
        }

        // A window parked on another virtual desktop, or a suspended UWP app, is a window but not one
        // the user is looking at.
        if (NativeMethods.DwmGetWindowAttribute(window, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
            && cloaked != 0)
        {
            return false;
        }

        // A maximized window keeps its title bar, and a title bar is a window that expects to be
        // moved around and closed - never what a fullscreen application asks for.
        var style = NativeMethods.GetWindowLong(window, NativeMethods.GWL_STYLE);
        if ((style & NativeMethods.WS_MAXIMIZE) != 0
            && (style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION)
        {
            return false;
        }

        var monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        return Covers(VisibleBounds(window), info.rcMonitor);
    }

    /// <summary>
    /// The part of a window the user sees. GetWindowRect counts the invisible resize border that DWM
    /// draws a few pixels outside the frame, which would make every maximized window look as if it
    /// filled its monitor; the extended frame bounds stop at the edge the user can actually see.
    /// </summary>
    private static NativeMethods.RECT VisibleBounds(IntPtr window)
    {
        var size = Marshal.SizeOf<NativeMethods.RECT>();
        if (NativeMethods.DwmGetWindowBounds(window, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var bounds, size) == 0)
        {
            return bounds;
        }

        return NativeMethods.GetWindowRect(window, out var rect) ? rect : default(NativeMethods.RECT);
    }

    /// <summary>True when <paramref name="bounds"/> reaches every edge of <paramref name="monitor"/>.</summary>
    private static bool Covers(NativeMethods.RECT bounds, NativeMethods.RECT monitor)
        => bounds.Left <= monitor.Left + Slack
            && bounds.Top <= monitor.Top + Slack
            && bounds.Right >= monitor.Right - Slack
            && bounds.Bottom >= monitor.Bottom - Slack;
}
