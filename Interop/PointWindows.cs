using System.Runtime.InteropServices;

namespace DevShell.Interop;

/// <summary>
/// Which top-level window is under the mouse. Dragging a desktop icon is normally a plain move, but
/// once the pointer is over somebody else's window the icon has to be handed over as a real shell
/// drag - and this is how that is told apart from a move inside the desktop.
/// </summary>
internal static class PointWindows
{
    private const uint GaRoot = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    /// <summary>The top-level window under the mouse, or <see cref="IntPtr.Zero"/> if there is none.</summary>
    public static IntPtr RootUnderCursor()
    {
        try
        {
            if (!GetCursorPos(out var point))
            {
                return IntPtr.Zero;
            }

            var window = WindowFromPoint(point);
            return window == IntPtr.Zero ? IntPtr.Zero : GetAncestor(window, GaRoot);
        }
        catch
        {
            // A hit test that fails just means "treat it as our own surface", which only ever costs
            // the user the shell drag; it must never take the desktop down.
            return IntPtr.Zero;
        }
    }
}
