using System.Windows;

namespace DevShell;

/// <summary>
/// What the bar's settings flyout is allowed to ask of the desktop surface. The settings themselves
/// live in the shared <see cref="Services.Config"/>, so all this does is make the surface catch up
/// with whatever the user just changed over there.
/// </summary>
public partial class DesktopWindow
{
    /// <summary>Re-reads the settings the desktop cares about: what is shown, the icon size, the wallpaper.</summary>
    internal void ApplySettingsChange()
    {
        ApplyIconVisibility();
        ApplyWallpaper(force: true);
        _ = RefreshIconsAsync();
    }

    /// <summary>Puts every icon back on the grid, the way Explorer's <i>Arrange icons</i> does.</summary>
    internal void ArrangeIconsFromSettings() => ArrangeIconsToGrid();

    /// <summary>Looks at the Desktop folder again, for the items that arrived behind our back.</summary>
    internal void RefreshFromSettings() => _ = RefreshIconsAsync();
}
