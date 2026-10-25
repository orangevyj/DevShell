using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DevShell.Interop;
using DevShell.Services;

namespace DevShell;

/// <summary>
/// Drag and drop for the desktop surface: files dragged in from Explorer land in the Desktop folder
/// on the grid cell under the pointer, and an icon dragged out onto another application is handed
/// over as a real shell drag, exactly as Explorer would hand it over.
/// </summary>
public partial class DesktopWindow
{
    /// <summary>Where the personal Desktop folder lives - the drop target for everything dropped in.</summary>
    private static string PersonalDesktopFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    // ---- dragging files in ----------------------------------------------

    private void Surface_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = FilesIn(e.Data) is { Length: > 0 }
            ? (IsCopying ? DragDropEffects.Copy : DragDropEffects.Move)
            : DragDropEffects.None;

        e.Handled = true;
    }

    private void Surface_Drop(object sender, DragEventArgs e)
    {
        if (FilesIn(e.Data) is not { Length: > 0 } files)
        {
            return;
        }

        e.Handled = true;

        var copy = IsCopying;
        var at = e.GetPosition(Surface);
        var added = Transfer(files, PersonalDesktopFolder, copy, at);

        if (added > 0)
        {
            App.LogInfo($"Desktop: {(copy ? "copied" : "moved")} {added} item(s) in from a drop");
            _ = RefreshIconsAsync();
        }
    }

    private static bool IsCopying => (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

    private static string[]? FilesIn(IDataObject? data)
        => data is not null && data.GetDataPresent(DataFormats.FileDrop)
            ? data.GetData(DataFormats.FileDrop) as string[]
            : null;

    // ---- dragging icons out ---------------------------------------------

    /// <summary>True when the item is a real path the shell can be handed; a virtual item cannot leave.</summary>
    private static bool CanLeaveDesktop(DesktopIcon icon) => icon.Item.Path is not null;

    /// <summary>
    /// Hands the item over as a real shell drag, so the application under the pointer receives a file
    /// drop - a move when the receiver takes the file, nothing at all when it refuses.
    /// </summary>
    /// <remarks>
    /// Who decides when this happens is the caller's business. The desktop asks on every move of a
    /// drag rather than once as the drag starts, because a drag that is leaving for another window
    /// begins with the pointer over our own surface - the only place a desktop icon can be picked up.
    /// </remarks>
    private void StartShellDrag(DesktopIcon icon)
    {
        if (icon.Item.Path is not { } path)
        {
            return;
        }

        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { path });
            data.SetData(PreferredDropEffectFormat, DropEffectStream(DropEffectCopyMove));

            App.LogInfo($"Desktop: handing '{icon.Item.DisplayName}' to another application");

            if (DragDrop.DoDragDrop(IconHost, data, DragDropEffects.Copy | DragDropEffects.Move)
                == DragDropEffects.Move)
            {
                // The receiver moved the file away; the Desktop watcher removes the icon for us.
                App.LogInfo($"Desktop: '{icon.Item.DisplayName}' was moved out");
            }
        }
        catch (Exception ex)
        {
            App.Log("Desktop.ShellDrag", ex);
        }
    }

    /// <summary>
    /// True while the mouse is over the desktop surface or the bar. Everything else - a real window,
    /// another shell surface - counts as somewhere the icon is allowed to be dragged out to.
    /// </summary>
    private static bool IsOverOurOwnWindows()
    {
        var under = PointWindows.RootUnderCursor();
        if (under == IntPtr.Zero)
        {
            return false;
        }

        if (Application.Current is not { } app)
        {
            return false;
        }

        if (app.MainWindow is { } bar && new WindowInteropHelper(bar).Handle == under)
        {
            return true;
        }

        return app.Windows.OfType<DesktopWindow>()
            .Any(window => new WindowInteropHelper(window).Handle == under);
    }

    // ---- the file transfer itself --------------------------------------

    /// <summary>
    /// Moves or copies items into a folder. When a landing point is given, the items are remembered
    /// one grid cell apart from it, so the refresh that follows puts them under the pointer.
    /// </summary>
    private int Transfer(IReadOnlyList<string> sources, string folder, bool copy, Point? at)
    {
        var landed = 0;
        var step = 0;

        foreach (var source in sources)
        {
            try
            {
                var isFolder = Directory.Exists(source);
                if (!isFolder && !File.Exists(source))
                {
                    continue;
                }

                var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var target = UniqueTarget(folder, name);
                if (isFolder)
                {
                    if (copy)
                    {
                        CopyFolder(source, target);
                    }
                    else
                    {
                        Directory.Move(source, target);
                    }
                }
                else if (copy)
                {
                    File.Copy(source, target);
                }
                else
                {
                    File.Move(source, target);
                }

                landed++;

                if (at is { } point)
                {
                    Remember(target, point, step++);
                }
            }
            catch (Exception ex)
            {
                App.Log($"Desktop.Transfer({source})", ex);
            }
        }

        if (at is not null && landed > 0)
        {
            _config.Save();
        }

        return landed;
    }

    /// <summary>Puts a newly arrived item on the grid cell nearest a point, a cell lower per item.</summary>
    private void Remember(string path, Point at, int step)
    {
        var width = Surface.ActualWidth > 0 ? Surface.ActualWidth : ActualWidth;
        var height = (Surface.ActualHeight > 0 ? Surface.ActualHeight : ActualHeight) - _config.BarHeight;
        var drop = at.Y + (step * DesktopLayout.CellHeight(_config.DesktopIconSize));

        var spot = DesktopLayout.Snap(at.X, drop, width, height, _config.DesktopIconSize);
        _config.DesktopPositions[path] = new[] { spot.X, spot.Y };
    }

    /// <summary>
    /// A name in the folder that nothing else is using yet - "report (2).txt" the way Explorer keeps
    /// both copies rather than silently replacing the one that is already there.
    /// </summary>
    private static string UniqueTarget(string folder, string name)
    {
        var target = Path.Combine(folder, name);
        if (!Exists(target))
        {
            return target;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({index}){extension}");
            if (!Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var folder in Directory.GetDirectories(source))
        {
            CopyFolder(folder, Path.Combine(target, Path.GetFileName(folder)));
        }
    }
}
