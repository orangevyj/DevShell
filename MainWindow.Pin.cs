using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DevShell.Interop;
using DevShell.Services;

namespace DevShell;

/// <summary>
/// Pinning. A pin is a shortcut in Explorer's own pinned folder, so the program lands on the taskbar
/// whichever shell is running: the bar takes a program dropped on it, a Start menu entry pins itself
/// from its context menu, and a pinned button unpins itself the same way.
/// </summary>
public partial class MainWindow
{
    private Point _dragStart;
    private StartApp? _dragApp;

    // ---- dropping a program on the bar -----------------------------------

    /// <summary>What a pinned button carries while it is being dragged along the bar.</summary>
    private const string PinFormat = "DevShell.Pin";

    private void Bar_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(PinFormat) ? DragDropEffects.Move
            : CanPin(e.Data) ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Bar_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        // A pin dragged along the bar: put it down where it was let go.
        if (e.Data.GetDataPresent(PinFormat) && e.Data.GetData(PinFormat) is string moved)
        {
            MovePinnedTo(moved, e.GetPosition(PinnedItems));
            return;
        }

        if (!CanPin(e.Data) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] paths ||
            paths.Length == 0)
        {
            return;
        }

        var failed = false;

        foreach (var path in paths)
        {
            if (PinnedApps.Pin(path, out var shortcut, out var error))
            {
                App.LogInfo($"Dropped and pinned: {path} -> {shortcut}");
            }
            else
            {
                failed = true;
                App.LogInfo($"Pin '{path}' failed: {error}");
            }
        }

        if (failed)
        {
            ShellActions.ShowError(Strings.T("Pin_Failed"));
        }

        LoadPinnedApps();
        CloseFlyouts();
    }

    /// <summary>A drop can carry anything at all; only something that exists can become a pin.</summary>
    private static bool CanPin(IDataObject data)
    {
        return data.GetDataPresent(DataFormats.FileDrop) &&
               data.GetData(DataFormats.FileDrop) is string[] paths &&
               paths.Length > 0 &&
               paths.All(File.Exists);
    }

    // ---- the menus -------------------------------------------------------

    private void AppMenuUnpin_Click(object sender, RoutedEventArgs e)
    {
        if (DataContextOf(sender) is not PinnedApp app)
        {
            return;
        }

        if (!PinnedApps.Unpin(app, out var error) && error is not null)
        {
            ShellActions.ShowError(error);
        }

        LoadPinnedApps();
    }

    private void StartAppPin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StartApp app)
        {
            return;
        }

        if (!PinnedApps.Pin(app.ShortcutPath, out _, out var error))
        {
            ShellActions.ShowError(error ?? Strings.T("Pin_Failed"));
            return;
        }

        LoadPinnedApps();
    }

    // ---- dragging a pin along the bar ------------------------------------

    private Point _pinDragStart;
    private PinnedApp? _pinDrag;

    private void PinnedStrip_DragStart(object sender, MouseButtonEventArgs e)
    {
        _pinDrag = PinButtonAt((DependencyObject)e.OriginalSource)?.DataContext as PinnedApp;
        _pinDragStart = e.GetPosition(this);
    }

    /// <summary>
    /// A pinned button turns into a drag once the pointer has moved far enough: the drag carries the
    /// shortcut it stands for, and the bar puts it down in whatever slot it was let go over.
    /// </summary>
    private void PinnedStrip_DragMove(object sender, MouseEventArgs e)
    {
        if (_pinDrag is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var now = e.GetPosition(this);
        if (Math.Abs(now.X - _pinDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _pinDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var app = _pinDrag;
        _pinDrag = null;

        DragDrop.DoDragDrop(PinnedItems, new DataObject(PinFormat, app.ShortcutPath), DragDropEffects.Move);
    }

    /// <summary>
    /// Moves a pin to the slot under the pointer: left of a button's middle drops before it, right of
    /// it drops after, and a point past the end of the strip sends it to the end. The order is saved.
    /// </summary>
    private void MovePinnedTo(string shortcutPath, Point pointInPinned)
    {
        var apps = PinnedApps.Load(_config.PinnedOrder).ToList();
        var from = apps.FindIndex(app => string.Equals(app.ShortcutPath, shortcutPath, StringComparison.OrdinalIgnoreCase));
        if (from < 0)
        {
            return;
        }

        var to = apps.Count;

        if (PinnedItems.InputHitTest(pointInPinned) is DependencyObject hit &&
            PinButtonAt(hit) is { } button &&
            button.DataContext is PinnedApp over)
        {
            var index = apps.FindIndex(app => string.Equals(app.ShortcutPath, over.ShortcutPath, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                var left = button.TranslatePoint(new Point(0, 0), PinnedItems).X;
                to = pointInPinned.X < left + button.ActualWidth / 2 ? index : index + 1;
            }
        }

        // The entry is lifted out before it is put down again, so a slot past it shifts down by one.
        var name = apps[from].Name;
        var before = apps.Select(app => app.ShortcutPath).ToList();
        MoveWithin(apps, from, to);
        if (apps.Select(app => app.ShortcutPath).SequenceEqual(before))
        {
            return;
        }

        _config.PinnedOrder = apps.Select(app => Path.GetFileName(app.ShortcutPath)).ToList();
        _config.Save();
        LoadPinnedApps();

        App.LogInfo($"Moved pin '{name}' in front of slot {to}");
    }

    /// <summary>
    /// Lifts the entry at <paramref name="from"/> out of the list and puts it down at
    /// <paramref name="slot"/>, a position counted in the list as it stands before the move: slot 3
    /// means before whatever is third, and a slot at the end lands after the last entry.
    /// </summary>
    internal static void MoveWithin<T>(IList<T> items, int from, int slot)
    {
        if (from < 0 || from >= items.Count)
        {
            return;
        }

        if (slot > from)
        {
            slot--;
        }

        slot = Math.Clamp(slot, 0, items.Count - 1);
        if (slot == from)
        {
            return;
        }

        var moved = items[from];
        items.RemoveAt(from);
        items.Insert(slot, moved);
    }

    /// <summary>The pinned button that owns an element inside the strip, if the point landed on one.</summary>
    private static FrameworkElement? PinButtonAt(DependencyObject start)
    {
        for (var node = start; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement element && element.DataContext is PinnedApp)
            {
                return element;
            }
        }

        return null;
    }

    // ---- dragging a program out of the Start menu ------------------------

    private void StartApp_DragStart(object sender, MouseButtonEventArgs e)
    {
        _dragApp = (sender as FrameworkElement)?.DataContext as StartApp;
        _dragStart = e.GetPosition(this);
    }

    /// <summary>
    /// Once the pointer has moved far enough, the row becomes a drag of its shortcut: drop it on the
    /// bar to pin it, or anywhere else that takes files.
    /// </summary>
    private void StartApp_DragMove(object sender, MouseEventArgs e)
    {
        if (_dragApp is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        // The move event belongs to the row under the pointer, which may not be the one that started.
        if (!ReferenceEquals((sender as FrameworkElement)?.DataContext, _dragApp))
        {
            return;
        }

        var now = e.GetPosition(this);
        if (Math.Abs(now.X - _dragStart.X) < 6 && Math.Abs(now.Y - _dragStart.Y) < 6)
        {
            return;
        }

        var app = _dragApp;
        _dragApp = null;

        var data = new DataObject(DataFormats.FileDrop, new[] { app.ShortcutPath });
        DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
    }
}
