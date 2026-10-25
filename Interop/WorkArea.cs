namespace DevShell.Interop;

/// <summary>
/// Keeps the strip of screen the bar sits on out of the work area, the way Windows' own taskbar does.
/// </summary>
/// <remarks>
/// A maximized window is not put where the user left it: Windows sizes it to the work area, so the
/// strip a taskbar keeps for itself is the strip a maximized window never reaches. DevShell's bar
/// keeps no strip of its own - it is simply positioned over the bottom of the monitor - and that is
/// invisible while Explorer runs, because Explorer's taskbar has taken the strip back already. With
/// DevShell as the shell there is no taskbar to do that, so the work area is still the whole monitor
/// and every maximized window ends up with its bottom edge underneath the bar.
///
/// So while DevShell is the shell it takes the strip itself: the work area is shrunk by the bar's
/// height and nothing else, which puts a maximized window's bottom edge exactly at the bar's top
/// edge, as if the bar were the taskbar. A fullscreen window is not affected - it asks for the
/// monitor rather than the work area - and the bar still stands down for it. Handing the strip back
/// is <see cref="Restore"/>, which the bar calls on its way out.
/// </remarks>
internal static class WorkArea
{
    /// <summary>The work area as it was before the bar took its strip, so it can be handed back.</summary>
    private static NativeMethods.RECT? _whole;

    /// <summary>Shrinks the work area by <paramref name="bottomPixels"/> at the bottom of the screen.</summary>
    public static void Reserve(int bottomPixels)
    {
        if (bottomPixels <= 0)
        {
            return;
        }

        try
        {
            if (Read() is not { } current)
            {
                return;
            }

            var first = _whole is null;
            var original = _whole ??= current;
            var wanted = new NativeMethods.RECT
            {
                Left = original.Left,
                Top = original.Top,
                Right = original.Right,
                Bottom = original.Bottom - bottomPixels,
            };

            // Nothing to say if the strip is already the bar's: this runs on every tick of the bar's
            // watch, and a work area re-set on each of them would be a broadcast nobody asked for.
            if (wanted.Bottom <= wanted.Top || Same(current, wanted))
            {
                return;
            }

            Write(wanted);

            if (first)
            {
                App.LogInfo($"WorkArea: the bottom {bottomPixels}px belong to the bar, so maximized windows stop above it");
            }
        }
        catch (Exception ex)
        {
            App.Log("WorkArea.Reserve", ex);
        }
    }

    /// <summary>Gives the strip back. Does nothing while the bar never took one.</summary>
    public static void Restore()
    {
        if (_whole is not { } original)
        {
            return;
        }

        _whole = null;

        try
        {
            Write(original);
            App.LogInfo("WorkArea: the strip the bar kept is given back");
        }
        catch (Exception ex)
        {
            App.Log("WorkArea.Restore", ex);
        }
    }

    /// <summary>The work area now, or null if Windows would not say.</summary>
    private static NativeMethods.RECT? Read()
    {
        var rect = default(NativeMethods.RECT);
        return NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref rect, 0) ? rect : null;
    }

    private static void Write(NativeMethods.RECT value)
        => NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETWORKAREA, 0, ref value, NativeMethods.SPIF_SENDCHANGE);

    private static bool Same(NativeMethods.RECT left, NativeMethods.RECT right)
        => left.Left == right.Left && left.Top == right.Top
            && left.Right == right.Right && left.Bottom == right.Bottom;
}
