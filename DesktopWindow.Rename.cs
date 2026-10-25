using System.IO;
using DevShell.Interop;
using DevShell.Services;

namespace DevShell;

/// <summary>
/// Renaming an item on the desktop. The shell's own menu cannot carry this entry: renaming belongs
/// to the view - Explorer's IShellView implements it - and DevShell is only the host for the item's
/// IContextMenu, which is why "Rename" is missing from it here. So DevShell brings its own, backed
/// by the same small box Explorer shows, and offers it from the item's menu and from F2.
/// </summary>
public partial class DesktopWindow
{
    /// <summary>
    /// The path that has to be selected after the next refresh. A rename changes an item's path, so
    /// the selection cannot follow it by path the way an ordinary refresh does.
    /// </summary>
    private string? _nextSelection;

    private bool RenameSelected() => _selected is { } icon && Rename(icon);

    private bool Rename(DesktopIcon icon)
    {
        if (icon.Item.Path is not { } path)
        {
            return false;
        }

        var current = Path.GetFileName(path);
        var dialog = new RenameDialog(path) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        var renamed = dialog.NewName;
        if (string.Equals(renamed, current, StringComparison.Ordinal))
        {
            // Nothing to do: the dialog is dismissed by the same names it opened with.
            return false;
        }

        try
        {
            // File.Move and Directory.Move are both a rename when only the name changes, and both
            // keep the item in place - no copy, no new file that the folders would notice.
            var target = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, renamed);
            if (Directory.Exists(path))
            {
                Directory.Move(path, target);
            }
            else
            {
                File.Move(path, target);
            }

            _nextSelection = target;
            App.LogInfo($"Desktop: '{current}' renamed to '{renamed}'");
            _ = RefreshIconsAsync();
            return true;
        }
        catch (Exception ex)
        {
            App.Log("Desktop.Rename", ex);
            ShellActions.ShowError(Strings.Format("Dlg_RenameFailed", current, ex.Message));
            return false;
        }
    }
}
