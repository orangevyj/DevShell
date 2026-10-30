using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DevShell.Interop;
using DevShell.Services;
using DevShell.UI;

namespace DevShell;

/// <summary>
/// The bar itself: shell window handling, docking, backdrop and the shell context menu.
/// Search lives in MainWindow.Search.cs, the right-hand status cluster in MainWindow.Status.cs.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Config _config;
    private readonly FileIndexer _indexer = new();
    private readonly NetworkMonitor _monitor = new();
    private readonly VolumeService _volume = new();
    private readonly DispatcherTimer _statusTimer = new();

    private AppBarDock? _appBar;
    private DispatcherTimer? _topmostTimer;
    private DispatcherTimer? _fullscreenTimer;
    private bool _acrylicActive;
    private bool _fullscreenHidden;
    private bool _barWantedByUser;

    public MainWindow()
    {
        _config = Config.Load();

        InitializeComponent();

        Height = _config.BarHeight;
        SynchronizeMenuState();

        // The bar itself is always-on-top, so its flyouts have to be as well - otherwise any
        // other window that takes focus would cover them.
        foreach (var popup in FlyoutPopups)
        {
            popup.Opened += OnPopupOpened;
        }

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        DpiChanged += OnDpiChanged;

        InitializeSearch();
        InitializeStatus();
        NotificationArea.Start();
        InitializeTray();
        LoadPinnedApps();
        InitializeRunningApps();
        InitializeStart();
        InitializeStartKey();

        // The XAML relabels itself through the table; these two panels hold strings built in code.
        Strings.Changed += ApplyLanguage;
    }

    // ---- startup --------------------------------------------------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            WindowEffects.HideFromAltTab(this);

            if (IsAppBarMode() && RegisterAppBar())
            {
                App.LogInfo($"Docked as AppBar at {_config.BarHeight:0}px");
            }
            else
            {
                PositionOverlay();
                StartTopmostWatch();
            }
        }
        catch (Exception ex)
        {
            App.Log("SourceInitialized", ex);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyBackdrop();

        if (_appBar is null)
        {
            PositionOverlay();
        }

        RefreshStatus();
        StartStatusTimer();
        StartFullscreenWatch();
        UpdateReservedStrip();
        BeginIndexing(rebuild: false);
    }

    /// <summary>
    /// Runs after the table switched language. Everything the XAML binds relabels itself; the
    /// strings below are set from code, so the code that sets them is asked again.
    /// </summary>
    private void ApplyLanguage()
    {
        SynchronizeShellMode();
        SynchronizeTroubleshooters();

        if (_startApps.Count > 0)
        {
            BuildStartRows();
        }

        if (StartInfoRows.Children.Count > 0)
        {
            FillStartInfo();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Strings.Changed -= ApplyLanguage;        _statusTimer.Stop();
        _topmostTimer?.Stop();
        _fullscreenTimer?.Stop();
        DisposeAppBar();
        WorkArea.Restore();
        CancelSearch();
        CancelIndexing();

        try
        {
            _config.BarHeight = Height;
            _config.Save();
        }
        catch (Exception ex)
        {
            App.Log("SaveConfig", ex);
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            var closed = CloseFlyouts();
            if (closed && SearchPopup.IsOpen)
            {
                // Escape with the search open just closes the flyout, text stays.
            }

            e.Handled = closed;
        }
    }

    /// <summary>Every flyout the bar owns. Used for close/hide and topmost bookkeeping.</summary>
    private System.Windows.Controls.Primitives.Popup[] FlyoutPopups =>
    [
        SearchPopup, StartPopup, ClockPopup, NetworkPopup, VolumePopup, ActivityPopup, TrayPopup,
        RunningPopup,
    ];

    /// <summary>
    /// WPF gives each flyout its own HWND. Owned windows follow their owner's z-order, but a
    /// popup opened while another application owns the foreground can still end up underneath
    /// it, so it is pushed above explicitly once it has opened.
    /// </summary>
    private void OnPopupOpened(object? sender, EventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.Popup popup)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => AssertPopupTopmost(popup)));
        }
    }

    private static void AssertPopupTopmost(System.Windows.Controls.Primitives.Popup popup)
    {
        if (popup.IsOpen && popup.Child is Visual child)
        {
            WindowEffects.AssertTopmost(child);
        }
    }

    private bool CloseFlyouts()
    {
        // The window preview has its own hover bookkeeping, so it is closed through its own path
        // rather than by flipping the popup behind its back.
        var any = CloseRunningPreview();

        foreach (var popup in FlyoutPopups)
        {
            if (popup.IsOpen)
            {
                popup.IsOpen = false;
                any = true;
            }
        }

        return any;
    }

    /// <summary>Flyout anchored to the right edge of the bar (status widgets).</summary>
    private void ShowFlyoutRight(System.Windows.Controls.Primitives.Popup popup, double width, double anchorOffsetFromRight)
        => ShowFlyout(popup, width, ActualWidth - width - anchorOffsetFromRight);

    /// <summary>Flyout anchored to the left edge of <paramref name="anchor"/> (search, start).</summary>
    private void ShowFlyoutLeft(System.Windows.Controls.Primitives.Popup popup, double width, FrameworkElement anchor)
        => ShowFlyout(popup, width, OffsetOf(anchor));

    /// <summary>
    /// Toggling flyout: opens it above the bar, or closes it when it was already open.
    /// </summary>
    private void ShowFlyout(System.Windows.Controls.Primitives.Popup popup, double width, double left)
    {
        var willOpen = !popup.IsOpen;
        CloseFlyouts();

        if (willOpen)
        {
            OpenFlyout(popup, width, left);
        }
    }

    /// <summary>
    /// Opens (or keeps open) the flyout above the bar. The height is measured after the first
    /// layout pass so the flyout never flashes in the wrong place.
    /// </summary>
    private void OpenFlyout(
        System.Windows.Controls.Primitives.Popup popup, double width, double left, double gap = 6)
    {
        popup.PlacementTarget = this;
        popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
        popup.HorizontalOffset = Math.Max(6, Math.Min(left, Math.Max(6, ActualWidth - width - 6)));

        if (!popup.IsOpen)
        {
            popup.VerticalOffset = -4000;
            popup.IsOpen = true;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!popup.IsOpen)
            {
                return;
            }

            var height = (popup.Child as FrameworkElement)?.ActualHeight ?? 0;
            if (height <= 0)
            {
                height = popup.Child.DesiredSize.Height;
            }

            popup.VerticalOffset = -gap - height;
        }));
    }

    /// <summary>Left edge of <paramref name="anchor"/> in bar coordinates.</summary>
    private double OffsetOf(FrameworkElement anchor)
    {
        try
        {
            return anchor.TransformToAncestor(this).Transform(new Point(0, 0)).X;
        }
        catch
        {
            return 6;
        }
    }

    private void PositionFlyouts()
    {
        if (SearchPopup.IsOpen)
        {
            SearchPopup.HorizontalOffset = Math.Max(6, OffsetOf(SearchPill));
        }
    }

    // ---- docking --------------------------------------------------------

    private bool IsAppBarMode()
        => string.Equals(_config.DockMode, "AppBar", StringComparison.OrdinalIgnoreCase);

    private bool RegisterAppBar()
    {
        try
        {
            var heightPixels = (int)Math.Round(_config.BarHeight * VisualTreeHelper.GetDpi(this).DpiScaleY);
            var dock = new AppBarDock(this);
            dock.Reserved += OnAppBarReserved;

            if (!dock.Register(Math.Max(24, heightPixels)))
            {
                dock.Dispose();
                return false;
            }

            _appBar = dock;
            return true;
        }
        catch (Exception ex)
        {
            App.Log("AppBar", ex);
            _appBar = null;
            return false;
        }
    }

    private void DisposeAppBar()
    {
        if (_appBar is null)
        {
            return;
        }

        try
        {
            _appBar.Reserved -= OnAppBarReserved;
            _appBar.Dispose();
        }
        catch (Exception ex)
        {
            App.Log("AppBarRemove", ex);
        }

        _appBar = null;
    }

    private void OnAppBarReserved(NativeMethods.RECT rect)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = rect.Left / dpi.DpiScaleX;
            Width = rect.Width / dpi.DpiScaleX;
            Top = rect.Top / dpi.DpiScaleY;
            Height = rect.Height / dpi.DpiScaleY;
            PositionFlyouts();
        }
        catch (Exception ex)
        {
            App.Log("AppBarReserved", ex);
        }
    }

    private void PositionOverlay()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var handle = new WindowInteropHelper(this).Handle;
            var monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };

            if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                var bounds = info.rcMonitor;
                Left = bounds.Left / dpi.DpiScaleX;
                Width = bounds.Width / dpi.DpiScaleX;
                Top = bounds.Bottom / dpi.DpiScaleY - Height;
            }
            else
            {
                var work = SystemParameters.WorkArea;
                Left = work.Left;
                Width = work.Width;
                Top = work.Bottom - Height;
            }

            PositionFlyouts();
        }
        catch (Exception ex)
        {
            App.Log("PositionOverlay", ex);
        }
    }

    private void StartTopmostWatch()
    {
        _topmostTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1.5),
        };

        _topmostTimer.Tick -= OnTopmostTick;
        _topmostTimer.Tick += OnTopmostTick;
        _topmostTimer.Start();
    }

    private void OnTopmostTick(object? sender, EventArgs e)
    {
        try
        {
            // Hidden to get out of a fullscreen app's way: there is nothing to raise, and nothing
            // about a window nobody can see that is worth repositioning.
            if (_fullscreenHidden)
            {
                return;
            }

            WindowEffects.AssertTopmost(this);

            // Flyouts live in their own HWNDs, so keep those above other windows too.
            foreach (var popup in FlyoutPopups)
            {
                if (popup.IsOpen)
                {
                    AssertPopupTopmost(popup);
                }
            }

            // A menu opened from a flyout has to be re-raised after them, or the flyout buries it.
            if (_flyoutMenu is { IsOpen: true } menu)
            {
                WindowEffects.AssertTopmost(menu);
            }

            // Also re-anchor when the desktop resolution, monitor or taskbar position changes.
            if (!IsAppBarMode())
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                var handle = new WindowInteropHelper(this).Handle;
                var monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
                var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
                {
                    var wantedTop = info.rcMonitor.Bottom / dpi.DpiScaleY - Height;
                    var wantedLeft = info.rcMonitor.Left / dpi.DpiScaleX;
                    var wantedWidth = info.rcMonitor.Width / dpi.DpiScaleX;

                    if (Math.Abs(wantedTop - Top) > 1 || Math.Abs(wantedLeft - Left) > 1 || Math.Abs(wantedWidth - Width) > 1)
                    {
                        Left = wantedLeft;
                        Width = wantedWidth;
                        Top = wantedTop;
                        PositionFlyouts();
                    }
                }
            }

            // Nothing else keeps the bar's strip out of the work area on a session whose shell is
            // DevShell, and a maximized window slides under the bar again the moment it goes missing.
            UpdateReservedStrip();
        }
        catch (Exception ex)
        {
            App.Log("TopmostTick", ex);
        }
    }

    /// <summary>
    /// Keeps the bottom strip of the screen out of the work area while DevShell is the shell, so a
    /// window the user maximizes stops at the bar's top edge instead of sliding underneath it.
    /// </summary>
    /// <remarks>
    /// Doing it only while DevShell is the shell is the point: beside Explorer the real taskbar has
    /// taken the strip back already, and a second one taken here would be a gap above that taskbar. A
    /// registered app bar reserves its own strip through the shell's app bar protocol, so nothing is
    /// taken while one is up. Asking on every tick is deliberate and cheap - the strip is written
    /// only when it has actually gone missing - so neither a fullscreen app, nor a program that
    /// resets the work area, nor a change of monitor leaves a maximized window under the bar.
    /// </remarks>
    private void UpdateReservedStrip()
    {
        try
        {
            if (_appBar is not null || !WindowsIntegration.IsDevShellTheShell())
            {
                WorkArea.Restore();
                return;
            }

            var heightPixels = (int)Math.Round(_config.BarHeight * VisualTreeHelper.GetDpi(this).DpiScaleY);
            WorkArea.Reserve(Math.Max(24, heightPixels));
        }
        catch (Exception ex)
        {
            App.Log("ReservedStrip", ex);
        }
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        // DpiChanged is a routed event and WPF raises it for every Image it measures, so without
        // this guard the bar redocked and rebuilt its backdrop once per icon for a DPI that had
        // not actually changed.
        if (!DpiChange.IsReal(e))
        {
            return;
        }

        try
        {
            if (_appBar is not null)
            {
                var heightPixels = (int)Math.Round(_config.BarHeight * e.NewDpi.DpiScaleY);
                _appBar.Apply(Math.Max(24, heightPixels));
            }
            else
            {
                PositionOverlay();
            }

            ApplyBackdrop();
        }
        catch (Exception ex)
        {
            App.Log("DpiChanged", ex);
        }
    }

    // ---- fullscreen apps ------------------------------------------------

    /// <summary>
    /// The bar steps aside while a fullscreen application is in front, and comes back when it is gone.
    /// The tick is short because both changes happen between two blinks - a game going fullscreen, a
    /// video leaving it - and a bar that lingered on top of the app for a second and a half would look
    /// like the bar that never got out of the way at all, which is what this watch is here to end.
    /// </summary>
    private void StartFullscreenWatch()
    {
        _fullscreenTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };

        _fullscreenTimer.Tick -= OnFullscreenTick;
        _fullscreenTimer.Tick += OnFullscreenTick;
        _fullscreenTimer.Start();
    }

    private void OnFullscreenTick(object? sender, EventArgs e)
    {
        try
        {
            // Explorer hosts packaged-app activation for as long as it is the shell, so DevShell has to
            // keep that host up in its place. It runs on this tick because this is the one tick that
            // beats in both dock modes; the check itself throttles to one look every few seconds.
            ShellAppRuntime.EnsureRunning();

            var fullscreen = FullscreenWindow.IsForegroundFullscreen();

            // Once the user has asked for the bar - the Windows key is the way to it while it is
            // hidden - it stays up until the fullscreen app is gone. That is what Windows' own
            // taskbar does, and it is the only way back to the bar without leaving the app.
            if (!fullscreen)
            {
                _barWantedByUser = false;
            }

            if (fullscreen == _fullscreenHidden || (fullscreen && _barWantedByUser))
            {
                return;
            }

            if (fullscreen)
            {
                HideForFullscreen();
            }
            else
            {
                RestoreAfterFullscreen();
            }
        }
        catch (Exception ex)
        {
            App.Log("FullscreenTick", ex);
        }
    }

    private void HideForFullscreen()
    {
        _fullscreenHidden = true;
        CloseFlyouts();
        Visibility = Visibility.Hidden;
        App.LogInfo("A fullscreen app is in front - the bar hides behind it");
    }

    private void RestoreAfterFullscreen()
    {
        _fullscreenHidden = false;
        Visibility = Visibility.Visible;

        if (_appBar is not null)
        {
            ApplyAppBarHeight();
        }
        else
        {
            PositionOverlay();
            WindowEffects.AssertTopmost(this);
        }

        App.LogInfo("The fullscreen app has gone - the bar is back");
    }

    /// <summary>
    /// Puts the bar back on screen for the user rather than for the fullscreen app: the Windows key
    /// was pressed, so the bar and its Start menu come up over it the way the real taskbar does.
    /// </summary>
    private void BringBarBackForUser()
    {
        if (!_fullscreenHidden)
        {
            return;
        }

        _barWantedByUser = true;
        RestoreAfterFullscreen();
    }

    /// <summary>Re-asserts the strip the shell reserved, at the current scale.</summary>
    private void ApplyAppBarHeight()
    {
        if (_appBar is null)
        {
            return;
        }

        var heightPixels = (int)Math.Round(_config.BarHeight * VisualTreeHelper.GetDpi(this).DpiScaleY);
        _appBar.Apply(Math.Max(24, heightPixels));
    }

    // ---- backdrop -------------------------------------------------------

    /// <summary>
    /// Paints the bar. The bar's own tint goes over a backdrop the DWM supplies - acrylic blur when it
    /// is asked for, a plain translucent gradient when it is not - and the settings slider scales that
    /// tint, which is what makes the bar see-through. Full opacity paints exactly the bar as designed.
    /// </summary>
    private void ApplyBackdrop()
    {
        var tint = Color.FromRgb(0x12, 0x14, 0x1B);
        var opacity = Math.Clamp(_config.BarOpacity, LeastBarOpacity, 1.0);

        // The overlay is the bar's own tint, over the backdrop. The transparency slider moves it.
        var overlay = (byte)Math.Round(0x38 * opacity);

        _acrylicActive = _config.Acrylic
            && WindowEffects.TryEnableAcrylic(this, tint, (byte)Math.Round(0xB4 * opacity));

        // Acrylic off but the bar asked to be see-through: DWM still has to supply the backdrop.
        var translucent = !_acrylicActive
            && opacity < 0.999
            && WindowEffects.TryEnableTransparentBackdrop(this, tint, (byte)Math.Round(0xD0 * opacity));

        if (_acrylicActive || translucent)
        {
            Background = Brushes.Transparent;
            BarSurface.Background = new SolidColorBrush(Color.FromArgb(
                _acrylicActive ? overlay : (byte)Math.Round(0xD0 * opacity), 0x12, 0x14, 0x1B));
        }
        else
        {
            Background = new SolidColorBrush(tint);
            BarSurface.Background = Brushes.Transparent;
        }

        if (_config.Acrylic && !_acrylicActive)
        {
            App.LogInfo("Acrylic backdrop unavailable - falling back to a solid bar background");
        }
        else if (_config.BarOpacity < 0.999 && !_acrylicActive && !translucent)
        {
            App.LogInfo("A see-through bar needs a backdrop this machine will not provide - keeping the solid bar");
        }
    }

    // ---- pinned apps ----------------------------------------------------

    private void LoadPinnedApps()
    {
        try
        {
            var apps = PinnedApps.Load(_config.PinnedOrder);
            PinnedItems.ItemsSource = apps;

            var order = apps.Select(app => Path.GetFileName(app.ShortcutPath)).ToList();
            if (!order.SequenceEqual(_config.PinnedOrder, StringComparer.OrdinalIgnoreCase))
            {
                _config.PinnedOrder = order;
                _config.Save();
            }
        }
        catch (Exception ex)
        {
            App.Log("PinnedApps", ex);
        }
    }

    /// <summary>Reloads the pinned strip after something outside the bar changed it.</summary>
    internal void ReloadPinnedApps() => LoadPinnedApps();

    private void App_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PinnedApp app)
        {
            return;
        }

        CloseFlyouts();
        var result = PinnedApps.Launch(app);
        if (result != LaunchResult.Success)
        {
            App.LogInfo($"Launch '{app.Name}' returned {result}");
        }
    }

    private void AppMenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (DataContextOf(sender) is PinnedApp app)
        {
            PinnedApps.Launch(app);
        }
    }

    private void AppMenuAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (DataContextOf(sender) is not PinnedApp app)
        {
            return;
        }

        var result = PinnedApps.LaunchAsAdministrator(app, out var error);
        if (result == LaunchResult.Failed && error is not null)
        {
            ShellActions.ShowError(error);
        }
    }

    private void AppMenuLocation_Click(object sender, RoutedEventArgs e)
    {
        if (DataContextOf(sender) is PinnedApp app)
        {
            PinnedApps.OpenLocation(app);
        }
    }

    private static object? DataContextOf(object sender)
        => (sender as FrameworkElement)?.DataContext;

    // ---- start button ---------------------------------------------------

    private void Start_Click(object sender, RoutedEventArgs e)
        => OpenStartMenu();

    // ---- shell context menu --------------------------------------------

    private void SynchronizeMenuState()
    {
        MenuAcrylic.IsChecked = _config.Acrylic;
        MenuDockOverlay.IsChecked = !IsAppBarMode();
        MenuDockAppBar.IsChecked = IsAppBarMode();
        MenuShowDesktop.IsChecked = _config.ShowDesktopLayer;
    }

    private void MenuShowDesktop_Click(object sender, RoutedEventArgs e)
    {
        _config.ShowDesktopLayer = MenuShowDesktop.IsChecked;
        _config.Save();

        if (Application.Current is App app)
        {
            app.ShowDesktopLayer();
        }

        App.LogInfo($"Desktop layer: {(_config.ShowDesktopLayer ? "shown" : "hidden")}");
    }

    private void MenuRebuildIndex_Click(object sender, RoutedEventArgs e)
        => BeginIndexing(rebuild: true);

    private void MenuAcrylic_Click(object sender, RoutedEventArgs e)
    {
        _config.Acrylic = MenuAcrylic.IsChecked;
        _config.Save();
        ApplyBackdrop();
    }

    private void MenuDockOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAppBarMode())
        {
            SynchronizeMenuState();
            return;
        }

        _config.DockMode = "Overlay";
        _config.Save();
        DisposeAppBar();
        PositionOverlay();
        StartTopmostWatch();
        SynchronizeMenuState();
        App.LogInfo("Dock mode: overlay");
    }

    private void MenuDockAppBar_Click(object sender, RoutedEventArgs e)
    {
        if (IsAppBarMode())
        {
            SynchronizeMenuState();
            return;
        }

        _config.DockMode = "AppBar";
        _config.Save();

        if (RegisterAppBar())
        {
            _topmostTimer?.Stop();
            App.LogInfo("Dock mode: reserved screen space");
        }
        else
        {
            _config.DockMode = "Overlay";
            _config.Save();
            PositionOverlay();
            StartTopmostWatch();
            ShellActions.ShowError("The shell refused to reserve space for the bar. Staying in overlay mode.");
        }

        SynchronizeMenuState();
    }

    private void MenuTaskManager_Click(object sender, RoutedEventArgs e)
    {
        if (ShellActions.Open("taskmgr.exe") == LaunchResult.Failed)
        {
            ShellActions.ShowError(Strings.T("Msg_TaskManagerFailed"));
        }
    }

    private void MenuOpenDataFolder_Click(object sender, RoutedEventArgs e)
        => ShellActions.Open(App.DataDirectory);

    private void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        CloseFlyouts();
        CancelIndexing();
        DisposeAppBar();
        Application.Current.Shutdown();
    }
}
