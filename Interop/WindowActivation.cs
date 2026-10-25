namespace DevShell.Interop;

/// <summary>
/// Bringing another application's window to the front from the bar, and folding it away again.
/// Windows only hands the foreground to a process whose window is already in front, which is
/// exactly the situation a click on the bar creates - but if that ever fails, the two input
/// queues are attached for the moment of the call so the switch still goes through.
/// </summary>
internal static class WindowActivation
{
    public static bool BringToFront(IntPtr window)
    {
        if (window == IntPtr.Zero || !NativeMethods.IsWindow(window))
        {
            return false;
        }

        if (NativeMethods.IsIconic(window))
        {
            NativeMethods.ShowWindowAsync(window, NativeMethods.SW_RESTORE);
        }

        if (NativeMethods.SetForegroundWindow(window))
        {
            return true;
        }

        var targetThread = NativeMethods.GetWindowThreadProcessId(window, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();

        if (targetThread == 0 || targetThread == currentThread)
        {
            NativeMethods.BringWindowToTop(window);
            return NativeMethods.SetForegroundWindow(window);
        }

        if (!NativeMethods.AttachThreadInput(currentThread, targetThread, true))
        {
            return NativeMethods.BringWindowToTop(window);
        }

        try
        {
            NativeMethods.BringWindowToTop(window);
            return NativeMethods.SetForegroundWindow(window);
        }
        finally
        {
            NativeMethods.AttachThreadInput(currentThread, targetThread, false);
        }
    }

    public static void Minimize(IntPtr window)
    {
        if (window != IntPtr.Zero && NativeMethods.IsWindow(window) && !NativeMethods.IsIconic(window))
        {
            NativeMethods.ShowWindowAsync(window, NativeMethods.SW_MINIMIZE);
        }
    }

    /// <summary>
    /// Asks the window to close, the way the taskbar's own Close does: the application still gets
    /// to put up its "save your work?" prompt instead of being killed.
    /// </summary>
    public static void Close(IntPtr window)
    {
        if (window != IntPtr.Zero && NativeMethods.IsWindow(window))
        {
            NativeMethods.PostMessage(window, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
