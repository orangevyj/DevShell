using System.Runtime.InteropServices;

namespace DevShell.Interop;

/// <summary>
/// Keeps the desktop layer pinned directly above the shell's desktop window (Progman) and below
/// every ordinary window. That is the slot a real desktop occupies: it shows whenever nothing
/// covers the screen, and it never gets in the way of a window that does.
/// </summary>
/// <remarks>
/// Underneath that, Windows keeps the desktop at a fixed place in the z-order; a window that is
/// never activated cannot climb above other applications, which is exactly what we want. When
/// DevShell ends up being the shell itself (no explorer.exe), there is no window to sit above,
/// so the layer goes to the very bottom instead.
/// </remarks>
internal static class DesktopPlacement
{
    public static void PinAboveShellDesktop(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return;
        }

        var shell = NativeMethods.GetShellWindow();

        // Sitting "above ourselves" would be meaningless: that is the DevShell-is-the-shell case.
        var insertAfter = shell != IntPtr.Zero && shell != window
            ? shell
            : NativeMethods.HWND_BOTTOM;

        var flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;

        if (!NativeMethods.SetWindowPos(window, insertAfter, 0, 0, 0, 0, flags))
        {
            App.Log("DesktopPlacement.SetWindowPos", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    /// <summary>Z-order position of a window, for diagnostics: 0 is the topmost window.</summary>
    public static int ZOrderOf(IntPtr window)
    {
        var candidate = NativeMethods.GetShellWindow();
        var index = 0;
        while (candidate != IntPtr.Zero && index < 5000)
        {
            if (candidate == window)
            {
                return index;
            }

            candidate = NativeMethods.GetWindow(candidate, NativeMethods.GW_HWNDNEXT);
            index++;
        }

        return -1;
    }
}
