using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DevShell.Interop;
using DevShell.Services;
using DevShell.UI;

namespace DevShell;

/// <summary>
/// The desktop surface: the real wallpaper with the icons from the user's Desktop folder and the
/// shared Public Desktop drawn on top, arranged in an Explorer-like grid.
/// </summary>
/// <remarks>
/// It is deliberately not topmost. Instead it is kept in the slot a desktop belongs in - directly
/// above the shell's own desktop window (Progman) and below every application window - so it is
/// visible whenever nothing covers the screen and never in the way when something does. Being a
/// tool window, it stays out of the taskbar and out of Alt+Tab.
/// </remarks>
public partial class DesktopWindow : Window
{
    private readonly Config _config;
    private readonly ObservableCollection<DesktopIcon> _icons = new();
    private readonly List<FileSystemWatcher> _watchers = new();

    private readonly DispatcherTimer _refreshDebounce = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(400),
    };

    private readonly DispatcherTimer _placementTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(3),
    };

    private readonly DispatcherTimer _wallpaperTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(60),
    };

    /// <summary>Everything selected, in the order it was picked; the last one is <see cref="_selected"/>.</summary>
    private readonly List<DesktopIcon> _selection = new();

    /// <summary>The icon the menus and the shortcuts act on: the one picked last.</summary>
    private DesktopIcon? _selected;

    private bool _enumerating;
    private bool _enumerateAgain;
    private string _wallpaperSignature = string.Empty;

    public DesktopWindow()
    {
        _config = Config.Load();

        InitializeComponent();
        IconHost.ItemsSource = _icons;
        ApplyIconVisibility();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        Activated += OnActivated;
        SizeChanged += OnSizeChanged;
        DpiChanged += OnDpiChanged;
    }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    // ---- startup --------------------------------------------------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            // Tool window: no taskbar button, no Alt+Tab entry.
            WindowEffects.HideFromAltTab(this);
            FitToMonitor();
            PinPlacement();
        }
        catch (Exception ex)
        {
            App.Log("Desktop.SourceInitialized", ex);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWallpaper(force: true);
        _ = RefreshIconsAsync();
        StartWatchers();

        _placementTimer.Tick += OnPlacementTick;
        _placementTimer.Start();

        _wallpaperTimer.Tick += OnWallpaperTick;
        _wallpaperTimer.Start();

        _refreshDebounce.Tick += OnRefreshDebounceTick;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshDebounce.Stop();
        _placementTimer.Stop();
        _wallpaperTimer.Stop();
        StopWatchers();
    }

    /// <summary>
    /// Clicking the desktop activates it, the same way Explorer's desktop takes focus, and
    /// activation can lift the window in the z-order - so the slot is restored right away.
    /// </summary>
    private void OnActivated(object? sender, EventArgs e) => PinPlacement();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged || e.HeightChanged)
        {
            ArrangeIcons();
        }
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        // DpiChanged is a routed event and WPF raises it for every Image it measures, so without
        // this guard the desktop redid its grid and its 3440x1440 wallpaper brush once per icon.
        if (!DpiChange.IsReal(e))
        {
            return;
        }

        // The wallpaper brush stretches itself to fit the surface, so only the grid needs redoing.
        ArrangeIcons();
    }

    // ---- window placement -----------------------------------------------

    private void PinPlacement() => DesktopPlacement.PinAboveShellDesktop(Handle);

    /// <summary>Cover the monitor this window is on, and re-cover it if the resolution changes.</summary>
    private void FitToMonitor()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var monitor = NativeMethods.MonitorFromWindow(Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };

            if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return;
            }

            var bounds = info.rcMonitor;
            var left = bounds.Left / dpi.DpiScaleX;
            var top = bounds.Top / dpi.DpiScaleY;
            var width = bounds.Width / dpi.DpiScaleX;
            var height = bounds.Height / dpi.DpiScaleY;

            if (Math.Abs(left - Left) > 1 || Math.Abs(top - Top) > 1
                || Math.Abs(width - Width) > 1 || Math.Abs(height - Height) > 1)
            {
                Left = left;
                Top = top;
                Width = width;
                Height = height;
            }
        }
        catch (Exception ex)
        {
            App.Log("Desktop.FitToMonitor", ex);
        }
    }

    private void OnPlacementTick(object? sender, EventArgs e)
    {
        try
        {
            FitToMonitor();
            PinPlacement();
        }
        catch (Exception ex)
        {
            App.Log("Desktop.PlacementTick", ex);
        }
    }

    private void OnWallpaperTick(object? sender, EventArgs e)
    {
        try
        {
            ApplyWallpaper(force: false);
        }
        catch (Exception ex)
        {
            App.Log("Desktop.WallpaperTick", ex);
        }
    }

    // ---- wallpaper ------------------------------------------------------

    private void ApplyWallpaper(bool force)
    {
        var info = WallpaperInfo.Detect(_config.WallpaperPath);
        var signature = $"{info.ImagePath}|{info.Fit}|{info.FallbackColor}";
        var changed = signature != _wallpaperSignature;

        // The minute timer runs without force and normally finds nothing to do.
        if (!changed && !force)
        {
            return;
        }

        _wallpaperSignature = signature;
        Surface.Background = CreateWallpaperBrush(info) ?? new SolidColorBrush(info.FallbackColor);

        // Only a change is worth a line; re-applying the very same brush stays quiet, which is what
        // keeps a busy startup from filling the log with identical entries.
        if (changed)
        {
            App.LogInfo($"Desktop wallpaper: {info.Origin} (fit {info.Fit})");
        }
    }

    private static Brush? CreateWallpaperBrush(WallpaperInfo info)
    {
        if (string.IsNullOrEmpty(info.ImagePath))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            // OnLoad reads the file up front so the wallpaper file is never left locked.
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(info.ImagePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap)
            {
                Stretch = info.Fit switch
                {
                    // Cover and crop, which is what Windows calls "Fill".
                    WallpaperFit.Fill or WallpaperFit.Span => Stretch.UniformToFill,
                    WallpaperFit.Fit => Stretch.Uniform,
                    WallpaperFit.Stretch => Stretch.Fill,
                    _ => Stretch.None,
                },
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
            };

            if (info.Fit == WallpaperFit.Tile)
            {
                // Tiling draws the image at its own pixel size and repeats it across the surface.
                brush.TileMode = TileMode.Tile;
                brush.ViewportUnits = BrushMappingMode.Absolute;
                brush.Viewport = new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
                brush.AlignmentX = AlignmentX.Left;
                brush.AlignmentY = AlignmentY.Top;
            }

            brush.Freeze();
            return brush;
        }
        catch (Exception ex)
        {
            App.Log($"Desktop.CreateWallpaperBrush({info.ImagePath})", ex);
            return null;
        }
    }

    // ---- icons ----------------------------------------------------------

    private async Task RefreshIconsAsync()
    {
        if (_enumerating)
        {
            // Something changed while the last pass was running; run once more when it is done.
            _enumerateAgain = true;
            return;
        }

        _enumerating = true;

        try
        {
            var includePublic = _config.DesktopIncludePublic;
            var includeRecycleBin = _config.DesktopShowRecycleBin;
            var showHidden = _config.DesktopShowHiddenItems;
            var showExtensions = _config.DesktopShowFileExtensions;

            var items = await Task.Run(
                () => DesktopItems.Enumerate(
                    includePublic, includeRecycleBin, showHidden, showExtensions));

            ApplyItems(items);
        }
        catch (Exception ex)
        {
            App.Log("Desktop.RefreshIcons", ex);
        }
        finally
        {
            _enumerating = false;

            if (_enumerateAgain)
            {
                _enumerateAgain = false;
                _ = RefreshIconsAsync();
            }
        }
    }

    private void ApplyItems(List<DesktopItem> items)
    {
        // Keep the selection if the same items are still there after the refresh - or follow it to
        // the name a rename just gave it.
        var previous = _nextSelection is { } renamed
            ? new[] { renamed }
            : _selection.Select(icon => icon.Item.Path).OfType<string>().ToArray();
        _nextSelection = null;

        _icons.Clear();
        foreach (var item in items)
        {
            // The window's own font family, so an icon can measure its name the way the label will.
            _icons.Add(new DesktopIcon(item, _config.DesktopIconSize, FontFamily));
        }

        ClearSelection();

        foreach (var icon in _icons)
        {
            if (icon.Item.Path is { } path && previous.Contains(path))
            {
                AddToSelection(icon);
            }
        }

        ArrangeIcons();
    }

    /// <summary>
    /// Places every icon: the remembered spot for one the user has dragged, the grid for the
    /// rest. The bar's strip is left out of the usable height so no icon hides behind it.
    /// </summary>
    private void ArrangeIcons() => ApplyPlacements();

    /// <summary>"Arrange icons" forgets the dragged positions and returns everything to the grid.</summary>
    private void ArrangeIconsToGrid()
    {
        _config.DesktopPositions.Clear();
        _config.Save();
        ApplyPlacements();
    }

    /// <summary>
    /// "Refresh desktop icons" looks at the Desktop folders again, so files that arrived without
    /// DevShell noticing - a download, a file another program wrote, a change the watcher missed -
    /// are added to the surface.
    /// </summary>
    private void RefreshDesktopIcons() => _ = RefreshIconsAsync();

    private void ApplyIconVisibility()
        => IconHost.Visibility = _config.DesktopShowIcons ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Replaces the selection with one icon, or clears it when nothing is named.</summary>
    private void Select(DesktopIcon? icon)
    {
        if (icon is null)
        {
            ClearSelection();
            return;
        }

        if (_selection.Count == 1 && ReferenceEquals(_selection[0], icon))
        {
            return;
        }

        ClearSelection();
        AddToSelection(icon);
    }

    /// <summary>
    /// Adds one icon to the selection. The icon added last is the one the menus and the shortcuts
    /// act on, which is how Explorer treats the anchor of a multi-selection too.
    /// </summary>
    private void AddToSelection(DesktopIcon icon)
    {
        _selected = icon;

        if (icon.IsSelected)
        {
            return;
        }

        icon.IsSelected = true;
        _selection.Add(icon);
    }

    /// <summary>Ctrl+click: one icon joins the selection or leaves it.</summary>
    private void ToggleInSelection(DesktopIcon icon)
    {
        if (_selection.Remove(icon))
        {
            icon.IsSelected = false;
            _selected = _selection.Count > 0 ? _selection[^1] : null;
            return;
        }

        AddToSelection(icon);
    }

    private void ClearSelection()
    {
        foreach (var icon in _selection)
        {
            icon.IsSelected = false;
        }

        _selection.Clear();
        _selected = null;
    }

    /// <summary>Ctrl+A: every icon on the surface.</summary>
    private void SelectAllIcons()
    {
        ClearSelection();

        foreach (var icon in _icons)
        {
            AddToSelection(icon);
        }
    }

    // ---- watching -------------------------------------------------------

    private void StartWatchers()
    {
        foreach (var folder in WatchedFolders())
        {
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName
                                 | NotifyFilters.DirectoryName
                                 | NotifyFilters.Attributes
                                 | NotifyFilters.LastWrite
                                 | NotifyFilters.Size,
                };

                watcher.Created += OnWatchedFolderChanged;
                watcher.Deleted += OnWatchedFolderChanged;
                watcher.Changed += OnWatchedFolderChanged;
                watcher.Renamed += OnWatchedFolderChanged;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                App.Log($"Desktop.StartWatchers({folder})", ex);
            }
        }
    }

    private void StopWatchers()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                App.Log("Desktop.StopWatchers", ex);
            }
        }

        _watchers.Clear();
    }

    private static IEnumerable<string> WatchedFolders()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(user))
        {
            yield return user;
        }

        var shared = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrEmpty(shared))
        {
            yield return shared;
        }
    }

    /// <summary>
    /// The watcher fires on a thread-pool thread and once per file, so every change is funnelled
    /// into a single short debounce on the UI thread.
    /// </summary>
    private void OnWatchedFolderChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
        }));
    }

    private void OnRefreshDebounceTick(object? sender, EventArgs e)
    {
        _refreshDebounce.Stop();
        _ = RefreshIconsAsync();
    }

    // ---- icon interaction -----------------------------------------------

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Only the wallpaper itself clears the selection; clicks on icons are handled by the icon.
        if (ReferenceEquals(e.OriginalSource, Surface))
        {
            Select(null);
        }
    }

    private void Icon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DesktopIcon icon)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            // Ctrl+click adds or removes one icon and starts no drag, so a selection being built
            // up cannot be moved by accident.
            ToggleInSelection(icon);
            e.Handled = true;
            return;
        }

        Select(icon);

        if (e.ClickCount == 2)
        {
            // Otherwise the second click also reaches the surface and clears the selection.
            e.Handled = true;
            Launch(icon);
        }
    }

    private void Icon_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DesktopIcon icon)
        {
            return;
        }

        // Explorer selects on right-click before the menu appears - but a right-click inside an
        // existing multi-selection leaves that selection alone, because the menu acts on all of it.
        if (icon.IsSelected)
        {
            _selected = icon;
        }
        else
        {
            Select(icon);
        }

        e.Handled = true;
        ShowIconMenu(icon);
    }

    /// <summary>
    /// The wallpaper's own menu. It is the shell's menu for the desktop folder itself, so it
    /// carries what Explorer puts there - New, Paste, Display settings, Personalize, and whatever
    /// shell extensions are installed - with DevShell's own entries appended: the ones that act on
    /// the desktop as a whole first, then the settings as one submenu.
    /// </summary>
    private void Surface_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Surface))
        {
            // A right-click that landed on an icon is handled by the icon.
            return;
        }

        e.Handled = true;

        // Refresh is a top-level entry, next to the shell's own: the shell's Refresh only repaints
        // Explorer's own view, which is not the one on screen, so the entry that actually looks at
        // the Desktop folder has to be visible on its own rather than tucked into the submenu.
        var extras = new List<ExtraMenuItem>
        {
            new(Strings.T("Desk_Refresh"), RefreshDesktopIcons),
            new(Strings.T("Desk_ChangeWallpaper"), ChangeWallpaper),
            new(Strings.T("Desk_WallpaperWindows"), ResetWallpaper),
            new(Strings.T("Desk_Arrange"), ArrangeIconsToGrid) { Group = DevShellMenu },
            new(Strings.T("Desk_ShowIcons"), ToggleShowIcons) { Group = DevShellMenu, IsChecked = _config.DesktopShowIcons },
            new(Strings.T("Desk_IncludePublic"), ToggleIncludePublic) { Group = DevShellMenu, IsChecked = _config.DesktopIncludePublic },
            new(Strings.T("Desk_ShowRecycleBin"), ToggleShowRecycleBin) { Group = DevShellMenu, IsChecked = _config.DesktopShowRecycleBin },
            new(Strings.T("Desk_ShowHidden"), ToggleShowHidden) { Group = DevShellMenu, IsChecked = _config.DesktopShowHiddenItems },
            new(Strings.T("Desk_OpenPersonal"), OpenPersonalFolder) { Group = DevShellMenu },
            new(Strings.T("Desk_OpenPublic"), OpenPublicFolder) { Group = DevShellMenu },
        };

        using var menu = new ShellContextMenu();
        menu.Show(Handle, null, extras);
    }

    /// <summary>
    /// The shell's menu for one icon. Explorer's entries (Open, Open with, Send to, Cut, Copy,
    /// Create shortcut, Delete, Properties, ...) come from the shell itself; DevShell adds the
    /// things the shell will not hand to a host that is not Explorer's own view - the rename, and
    /// the two that only DevShell can do.
    /// </summary>
    private void ShowIconMenu(DesktopIcon icon)
    {
        var extras = new List<ExtraMenuItem>();
        if (icon.Item.Path is { } path)
        {
            // Rename first: it is the one Explorer would have offered in this place in its menu.
            extras.Add(new ExtraMenuItem(Strings.T("Dlg_Rename"), () => Rename(icon)));
            extras.Add(new ExtraMenuItem(Strings.T("Hit_Admin"), () => OpenAsAdministrator(path)));
            extras.Add(new ExtraMenuItem(Strings.T("Hit_CopyPath"), () => CopyPath(path)));
            extras.Add(new ExtraMenuItem(Strings.T("Pin_Add"), () => PinToTaskbar(path)));
        }

        using var menu = new ShellContextMenu();
        menu.Show(Handle, icon.Item.OpenTarget, extras);
    }

    private static void Launch(DesktopIcon icon)
    {
        var result = ShellActions.Open(icon.Item.OpenTarget);
        if (result != LaunchResult.Success)
        {
            App.LogInfo($"Desktop: launching '{icon.Item.DisplayName}' returned {result}");
        }
    }

    // ---- menu actions ---------------------------------------------------
    // The shell's menus carry Explorer's own items; these are the entries DevShell adds to them.

    private const string DevShellMenu = "DevShell";

    private void ToggleShowIcons()
    {
        _config.DesktopShowIcons = !_config.DesktopShowIcons;
        _config.Save();
        ApplyIconVisibility();
    }

    private void ToggleIncludePublic()
    {
        _config.DesktopIncludePublic = !_config.DesktopIncludePublic;
        _config.Save();
        _ = RefreshIconsAsync();
    }

    private void ToggleShowRecycleBin()
    {
        _config.DesktopShowRecycleBin = !_config.DesktopShowRecycleBin;
        _config.Save();
        _ = RefreshIconsAsync();
    }

    private void ToggleShowHidden()
    {
        _config.DesktopShowHiddenItems = !_config.DesktopShowHiddenItems;
        _config.Save();
        _ = RefreshIconsAsync();
    }

    private void OpenPersonalFolder()
        => ShellActions.Open(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

    private void OpenPublicFolder()
        => ShellActions.Open(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));

    private static void OpenAsAdministrator(string path)
    {
        var result = ShellActions.OpenAsAdministrator(path, out var error);
        if (result == LaunchResult.Failed && error is not null)
        {
            ShellActions.ShowError(error);
        }
    }

    /// <summary>
    /// Pins a desktop file or shortcut to the bar. The bar reads the pin folder itself, so it is told
    /// to reload rather than guessing that it will notice.
    /// </summary>
    private static void PinToTaskbar(string path)
    {
        if (!PinnedApps.Pin(path, out _, out var error))
        {
            ShellActions.ShowError(error ?? Strings.T("Pin_Failed"));
            return;
        }

        if ((Application.Current as App)?.MainWindow is MainWindow bar)
        {
            bar.ReloadPinnedApps();
        }
    }

    private static void CopyPath(string path)
    {
        try
        {
            Clipboard.SetText(path);
        }
        catch (Exception ex)
        {
            App.Log("Desktop.CopyPath", ex);
        }
    }

    private void ChangeWallpaper()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Strings.T("Desk_ChooseWallpaper"),
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _config.WallpaperPath = dialog.FileName;
        _config.Save();
        ApplyWallpaper(force: true);
    }

    private void ResetWallpaper()
    {
        _config.WallpaperPath = null;
        _config.Save();
        ApplyWallpaper(force: true);
    }
}

/// <summary>
/// One icon on the surface. Only the grid position and the selection change after creation, so
/// those are the only two things that raise change notifications.
/// </summary>
internal sealed class DesktopIcon : INotifyPropertyChanged
{
    /// <summary>The size the label is drawn at, in XAML and in the measurement - keep them equal.</summary>
    private const double LabelFontSize = 12;

    /// <summary>Room left either side of the name inside the tile, so it is not flush with the edge.</summary>
    private const double LabelPadding = 16;

    /// <summary>How wide a selected tile may get before the name wraps instead of widening further.</summary>
    private const double MaxSelectedWidth = 240;

    private double _x;
    private double _y;
    private double _surfaceWidth;
    private bool _isSelected;

    /// <summary>The font the name is drawn in - the same one, so the tile can be sized to the name.</summary>
    private readonly FontFamily _labelFont;

    private string? _nameMeasured;
    private double _nameWidth;

    public DesktopIcon(DesktopItem item, double iconSize, FontFamily labelFont)
    {
        Item = item;
        IconSize = iconSize;
        _labelFont = labelFont;
        CellWidth = DesktopLayout.CellWidth(iconSize);
        CellHeight = DesktopLayout.CellHeight(iconSize);
    }

    public DesktopItem Item { get; }

    public double IconSize { get; }

    public double CellWidth { get; }

    public double CellHeight { get; }

    public string DisplayName => Item.DisplayName;

    public ImageSource? Icon => Item.Icon;

    public string Tooltip => Item.Tooltip;

    public double X
    {
        get => _x;
        set
        {
            if (Set(ref _x, value))
            {
                // The container is placed from ContainerX, not from X, and a computed property has to
                // announce itself: without this the container keeps the value ContainerX had when it
                // was built - zero, because every icon is seated only after its container exists -
                // and the whole desktop piles up in the first column.
                Raise(nameof(ContainerX));
            }
        }
    }

    public double Y
    {
        get => _y;
        set => Set(ref _y, value);
    }

    /// <summary>
    /// The width the label may take once the icon is selected: as wide as the name actually needs,
    /// never narrower than a cell and never wider than <see cref="MaxSelectedWidth"/>. The chip has
    /// to take the same width, because it is the slot the label is laid out in - a label wider than
    /// its slot is clipped by the layout, whatever ClipToBounds says - and a name longer than the
    /// cap wraps inside it, which is still the whole name.
    /// </summary>
    public double SelectedLabelWidth
    {
        get
        {
            var name = DisplayName;
            if (!string.Equals(_nameMeasured, name, StringComparison.Ordinal))
            {
                _nameMeasured = name;
                _nameWidth = MeasureName(name);
            }

            // The upper bound is only ever lifted by an icon size set past the cap in the config file;
            // Math.Clamp throws if the minimum is above the maximum, and this runs inside a binding.
            var cap = Math.Max(CellWidth, MaxSelectedWidth);
            return Math.Clamp(_nameWidth + LabelPadding, CellWidth, cap);
        }
    }

    /// <summary>
    /// How wide the name is on one line, measured with the same font and size the label is drawn in
    /// so the tile is built around the name instead of picked in advance.
    /// </summary>
    private double MeasureName(string name)
    {
        var probe = new TextBlock
        {
            Text = name,
            FontFamily = _labelFont,
            FontSize = LabelFontSize,
            TextWrapping = TextWrapping.NoWrap,
        };

        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    /// <summary>
    /// Where the container goes. Unselected that is the cell itself; selected the chip is
    /// <see cref="SelectedLabelWidth"/> wide instead of a cell wide, so the container moves half
    /// that difference to the left and the wider label stays centred on its own cell - as far as
    /// the surface allows, because the first and last columns have no room to spare and a label
    /// hanging off the screen is cut off again, just by the screen edge this time.
    /// </summary>
    public double ContainerX
    {
        get
        {
            if (!IsSelected)
            {
                return _x;
            }

            var centred = _x - ((SelectedLabelWidth - CellWidth) / 2);
            var furthest = _surfaceWidth > SelectedLabelWidth ? _surfaceWidth - SelectedLabelWidth : double.MaxValue;

            return Math.Clamp(centred, 0, furthest);
        }
    }

    /// <summary>
    /// How wide the desktop surface is, so a selected tile can be kept inside it.
    /// </summary>
    public double SurfaceWidth
    {
        get => _surfaceWidth;
        set
        {
            if (Set(ref _surfaceWidth, value))
            {
                Raise(nameof(ContainerX));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value))
            {
                Raise(nameof(ContainerX));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(property!);
        return true;
    }
}
