using System.Windows;

namespace DevShell.UI;

/// <summary>
/// Separates a real per-monitor DPI change from the routed-event noise WPF sends along with it.
/// </summary>
/// <remarks>
/// <c>DpiChanged</c> is a bubbling routed event, so a handler attached to a window also runs for
/// events raised deeper in the visual tree. <c>Image.MeasureOverride</c> raises one for every image
/// it measures while laying out, which is how both windows in DevShell ended up handling a DPI
/// "change" once per icon - 47 times per startup on the desktop layer, 6 times on the bar - with
/// <c>OldDpi</c> equal to <c>NewDpi</c> and nothing having changed at all.
/// </remarks>
internal static class DpiChange
{
    /// <summary>
    /// True only when the reported DPI is actually different, i.e. the window really did move to a
    /// monitor with another scale factor.
    /// </summary>
    public static bool IsReal(DpiChangedEventArgs e)
        => e.OldDpi.PixelsPerInchX != e.NewDpi.PixelsPerInchX
        || e.OldDpi.PixelsPerInchY != e.NewDpi.PixelsPerInchY;
}
