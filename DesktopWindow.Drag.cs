using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevShell.Services;

namespace DevShell;

/// <summary>
/// Dragging desktop icons. A position is written to config.json the moment the icon is let go, so
/// the layout survives a restart; <i>Arrange icons</i> is what hands the icons back to the grid.
/// </summary>
/// <remarks>
/// The press is noticed in a preview handler but the mouse is only captured once the pointer has
/// actually moved, so a plain click stays a click - capture from the press would make every
/// double-click launch a drag and cost the icon its usual click behaviour.
/// </remarks>
public partial class DesktopWindow
{
    /// <summary>How far the pointer has to travel before a press counts as a drag.</summary>
    private const double DragThreshold = 4;

    private DesktopIcon? _dragIcon;
    private Point _dragGrab;
    private bool _dragged;

    /// <summary>What the current drag moves: the pressed icon alone, or the whole selection.</summary>
    private readonly List<(DesktopIcon Icon, double X, double Y)> _dragGroup = new();

    /// <summary>Where a press that may draw a selection box started, in surface coordinates.</summary>
    private Point? _bandStart;
    private bool _banding;
    private bool _bandSelectsAdditive;

    private static bool CtrlHeld => (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

    private double SurfaceWidth => Surface.ActualWidth > 0 ? Surface.ActualWidth : ActualWidth;

    private double UsableHeight
        => (Surface.ActualHeight > 0 ? Surface.ActualHeight : ActualHeight) - _config.BarHeight;

    /// <summary>What a remembered position is filed under: the file itself, or the item's shell name.</summary>
    private static string KeyOf(DesktopIcon icon) => icon.Item.Path ?? icon.Item.OpenTarget;

    /// <summary>Every icon's spot: the remembered one if it was ever dragged, the grid otherwise.</summary>
    /// <remarks>
    /// The icons the user put somewhere by hand are seated first and keep their spot. Everything else
    /// takes the free cell nearest the one the grid would have given it, and never a cell that is
    /// already taken - which is also what repairs a layout saved back when two icons could still land
    /// on the same tile: the first of the pair keeps it, the other moves next door.
    /// </remarks>
    private void ApplyPlacements()
    {
        var width = Surface.ActualWidth > 0 ? Surface.ActualWidth : ActualWidth;
        var surfaceHeight = Surface.ActualHeight > 0 ? Surface.ActualHeight : ActualHeight;
        var usable = Math.Max(120, surfaceHeight - _config.BarHeight);
        var placements = DesktopLayout.Arrange(_icons.Count, usable, _config.DesktopIconSize);

        // Told once, up front: a selected icon shows its whole name in a tile wider than its cell,
        // and this is what that tile has to stay inside.
        foreach (var icon in _icons)
        {
            icon.SurfaceWidth = width;
        }

        var taken = new List<Point>(_icons.Count);
        var byHand = new HashSet<DesktopIcon>();

        foreach (var icon in _icons)
        {
            if (!_config.DesktopPositions.TryGetValue(KeyOf(icon), out var remembered) || remembered.Length != 2)
            {
                continue;
            }

            var spot = DesktopLayout.NearestFreeCell(
                remembered[0], remembered[1], width, usable, _config.DesktopIconSize, taken);

            icon.X = spot.X;
            icon.Y = spot.Y;
            taken.Add(spot);
            byHand.Add(icon);
        }

        for (var index = 0; index < _icons.Count && index < placements.Count; index++)
        {
            var icon = _icons[index];

            if (byHand.Contains(icon))
            {
                continue;
            }

            var spot = DesktopLayout.NearestFreeCell(
                placements[index].X, placements[index].Y, width, usable, _config.DesktopIconSize, taken);

            icon.X = spot.X;
            icon.Y = spot.Y;
            taken.Add(spot);
        }
    }

    /// <summary>
    /// Notes what a press landed on. On the wallpaper that is the start of a selection box; on an
    /// icon, it is the icon - and if that icon is part of a multi-selection, the whole selection
    /// moves. Nothing is captured yet - see the remarks above.
    /// </summary>
    private void Surface_PreviewLeftDown(object sender, MouseButtonEventArgs e)
    {
        _dragged = false;
        _dragIcon = null;
        _dragGroup.Clear();
        _bandStart = null;
        _banding = false;

        var at = e.GetPosition(Surface);
        _bandSelectsAdditive = CtrlHeld;

        if (IconUnder(e.OriginalSource) is not { } icon)
        {
            // Empty space: the press may grow into a selection box, and a plain click on the
            // wallpaper clears the selection exactly as it always did.
            _bandStart = at;

            if (!_bandSelectsAdditive)
            {
                ClearSelection();
            }

            return;
        }

        if (CtrlHeld)
        {
            // Ctrl+click only changes the selection; the icon's own handler does that, and the
            // press must not start a drag that would move what was just added.
            return;
        }

        if (icon.IsSelected)
        {
            // Pressing inside a multi-selection keeps it; that is the group about to be dragged.
            _selected = icon;
        }
        else
        {
            Select(icon);
        }

        _dragIcon = icon;
        _dragGrab = new Point(at.X - icon.X, at.Y - icon.Y);

        var members = icon.IsSelected && _selection.Count > 1
            ? _selection
            : new List<DesktopIcon> { icon };

        foreach (var member in members)
        {
            _dragGroup.Add((member, member.X, member.Y));
        }
    }

    private void Surface_DragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var at = e.GetPosition(Surface);

        if (_dragIcon is not { } icon)
        {
            UpdateBand(at);
            return;
        }

        var x = at.X - _dragGrab.X;
        var y = at.Y - _dragGrab.Y;

        if (!_dragged)
        {
            if (Math.Abs(x - icon.X) < DragThreshold && Math.Abs(y - icon.Y) < DragThreshold)
            {
                return;
            }

            // From here on it is a drag inside the desktop: the icon follows the pointer past its
            // own cell and the click that ends it launches nothing.
            _dragged = true;
            Surface.CaptureMouse();
            Cursor = Cursors.SizeAll;
        }

        // Past somebody else's window the drag belongs to the shell, not to us - and that is asked on
        // every move rather than once as the drag starts. A drag that is on its way out to another
        // application begins with the pointer over our own surface, which is the only place a desktop
        // icon can be picked up: asked once at the start, the answer is always "ours", and the icon is
        // only ever moved around the desktop instead of being dropped on the window under it.
        if (CanLeaveDesktop(icon) && !IsOverOurOwnWindows())
        {
            HandOverToShell(icon);
            return;
        }

        MoveDragged(x, y);
    }

    /// <summary>
    /// Moves what the press picked up: the icon under the pointer, or the whole group it belongs to
    /// when it was pressed inside a multi-selection. A group keeps its shape - every icon takes the
    /// same offset - and the offset stops where the first of them would leave the surface.
    /// </summary>
    private void MoveDragged(double x, double y)
    {
        var (dx, dy) = ClampGroupOffset(x - _dragGroup[0].X, y - _dragGroup[0].Y);

        foreach (var member in _dragGroup)
        {
            member.Icon.X = member.X + dx;
            member.Icon.Y = member.Y + dy;
        }
    }

    /// <summary>The furthest the group may travel without any of it leaving the surface.</summary>
    private (double X, double Y) ClampGroupOffset(double dx, double dy)
    {
        double left = double.MaxValue, right = double.MinValue;
        double top = double.MaxValue, bottom = double.MinValue;

        foreach (var (icon, x, y) in _dragGroup)
        {
            left = Math.Min(left, x);
            right = Math.Max(right, x + icon.CellWidth);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y + icon.CellHeight);
        }

        // The lower bound clamps first: a group wider than the surface has no offset that fits it,
        // and Math.Clamp throws when the minimum is above the maximum.
        var minX = -left;
        var minY = -top;

        return (
            Math.Clamp(dx, minX, Math.Max(minX, SurfaceWidth - right)),
            Math.Clamp(dy, minY, Math.Max(minY, UsableHeight - bottom)));
    }

    /// <summary>
    /// Lets the shell take over the drag: whatever the drag had already moved goes back - a receiver
    /// that refuses the drop must not leave the desktop scattered - and the receiver owns the mouse
    /// from there, so nothing of the move that would have followed is saved.
    /// </summary>
    private void HandOverToShell(DesktopIcon icon)
    {
        _dragged = false;
        _dragIcon = null;
        Surface.ReleaseMouseCapture();
        Cursor = Cursors.Arrow;

        foreach (var member in _dragGroup)
        {
            member.Icon.X = member.X;
            member.Icon.Y = member.Y;
        }

        _dragGroup.Clear();

        StartShellDrag(icon);
    }

    private void Surface_PreviewLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_banding)
        {
            FinishBand();
            return;
        }

        _bandStart = null;

        var icon = _dragIcon;
        _dragIcon = null;

        if (!_dragged)
        {
            _dragGroup.Clear();
            return;
        }

        _dragged = false;
        Surface.ReleaseMouseCapture();
        Cursor = Cursors.Arrow;

        if (icon is not null)
        {
            LandGroup(icon);
        }

        _dragGroup.Clear();
    }

    /// <summary>
    /// Grows the selection box from where the press started, once the pointer has moved. The press
    /// itself only clears the selection; nothing is drawn for a click.
    /// </summary>
    private void UpdateBand(Point at)
    {
        if (_bandStart is not { } start)
        {
            return;
        }

        if (!_banding)
        {
            if (Math.Abs(at.X - start.X) < DragThreshold && Math.Abs(at.Y - start.Y) < DragThreshold)
            {
                return;
            }

            _banding = true;
            Surface.CaptureMouse();
        }

        var box = new Rect(start, at);

        Canvas.SetLeft(SelectionBand, box.X);
        Canvas.SetTop(SelectionBand, box.Y);
        SelectionBand.Width = box.Width;
        SelectionBand.Height = box.Height;
        SelectionBand.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Turns the box into a selection: every icon whose cell the box touches joins it. Explorer
    /// replaces the selection unless Ctrl was held when the box was started, and this does the same.
    /// </summary>
    private void FinishBand()
    {
        var box = new Rect(
            Canvas.GetLeft(SelectionBand),
            Canvas.GetTop(SelectionBand),
            SelectionBand.Width,
            SelectionBand.Height);

        _banding = false;
        _bandStart = null;
        SelectionBand.Visibility = Visibility.Collapsed;
        Surface.ReleaseMouseCapture();
        Cursor = Cursors.Arrow;

        if (!_bandSelectsAdditive)
        {
            ClearSelection();
        }

        foreach (var icon in _icons)
        {
            if (new Rect(icon.X, icon.Y, icon.CellWidth, icon.CellHeight).IntersectsWith(box))
            {
                AddToSelection(icon);
            }
        }
    }

    /// <summary>
    /// Lands what was dragged on the grid cells nearest to where it was let go, so the icons keep
    /// the same tidy columns Explorer's do while still being placeable anywhere.
    /// </summary>
    /// <remarks>
    /// A tile holds one icon: let go on a cell somebody else is already on and the icon slides to the
    /// nearest free one instead. Nothing else moves out of the way, and the icon that got there first
    /// keeps its spot. A group is placed one icon at a time, the pressed one first, so a group let go
    /// where the grid cannot hold it keeps its shape as far as the free cells allow.
    /// </remarks>
    private void LandGroup(DesktopIcon primary)
    {
        var width = SurfaceWidth;
        var height = UsableHeight;

        var taken = new List<Point>(_icons.Count);
        foreach (var icon in _icons)
        {
            if (!_dragGroup.Any(member => ReferenceEquals(member.Icon, icon)))
            {
                taken.Add(new Point(icon.X, icon.Y));
            }
        }

        foreach (var member in _dragGroup.OrderBy(member => ReferenceEquals(member.Icon, primary) ? 0 : 1))
        {
            var spot = DesktopLayout.NearestFreeCell(
                member.Icon.X, member.Icon.Y, width, height, _config.DesktopIconSize, taken);

            member.Icon.X = spot.X;
            member.Icon.Y = spot.Y;
            taken.Add(spot);
            RecordPosition(member.Icon);
        }

        // One write for the whole drop, however many icons came down with it.
        _config.Save();
    }

    /// <summary>Files an icon's spot under the item it stands for.</summary>
    private void RecordPosition(DesktopIcon icon)
        => _config.DesktopPositions[KeyOf(icon)] = new[] { icon.X, icon.Y };

    /// <summary>The icon a press landed on, or null when it landed on the wallpaper itself.</summary>
    private static DesktopIcon? IconUnder(object? source)
    {
        for (var element = source as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is FrameworkElement { DataContext: DesktopIcon icon })
            {
                return icon;
            }
        }

        return null;
    }
}
