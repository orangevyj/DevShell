using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using DevShell.Interop;
using DevShell.Services;

namespace DevShell;

/// <summary>
/// The running-application buttons on the bar, next to the pinned programs. Everything here reads
/// the window manager, so an application gets a button only while it has windows on screen;
/// pinned programs stay pinned whether they run or not.
/// </summary>
public partial class MainWindow
{
    /// <summary>Width of the window preview card, in DIPs.</summary>
    private const double PreviewWidth = 320;

    /// <summary>How large a window is captured for the preview; the card scales it down.</summary>
    private const int PreviewCaptureWidth = 300;
    private const int PreviewCaptureHeight = 170;

    private readonly DispatcherTimer _windowTimer = new();
    private readonly DispatcherTimer _previewOpenTimer = new();
    private readonly DispatcherTimer _previewCloseTimer = new();

    private string _runningSignature = string.Empty;
    private RunningApp? _previewApp;
    private FrameworkElement? _previewAnchor;
    private int _previewGeneration;

    private void InitializeRunningApps()
    {
        _windowTimer.Interval = TimeSpan.FromSeconds(1);
        _windowTimer.Tick += (_, _) => RefreshRunningApps();
        _windowTimer.Start();

        // The preview follows the pointer: it comes up after a short rest on a grouped button and
        // folds away once the pointer has left both the button and the card. Both delays exist so
        // that crossing a button on the way to the next one does not raise a preview at all.
        _previewOpenTimer.Interval = TimeSpan.FromMilliseconds(350);
        _previewOpenTimer.Tick += (_, _) =>
        {
            _previewOpenTimer.Stop();
            OpenRunningPreview();
        };

        _previewCloseTimer.Interval = TimeSpan.FromMilliseconds(300);
        _previewCloseTimer.Tick += (_, _) =>
        {
            _previewCloseTimer.Stop();
            if (!RunningPreviewCard.IsMouseOver)
            {
                CloseRunningPreview();
            }
        };

        Closed += (_, _) =>
        {
            _windowTimer.Stop();
            _previewOpenTimer.Stop();
            _previewCloseTimer.Stop();
        };

        RefreshRunningApps();
    }

    /// <summary>
    /// Rebuilds the buttons only when the set of windows changed; otherwise only the "which app is
    /// in front" marker is refreshed, so a button never moves out from under the pointer.
    /// </summary>
    private void RefreshRunningApps()
    {
        try
        {
            var apps = RunningApps.Snapshot();
            var signature = RunningApps.SignatureOf(apps);

            if (signature != _runningSignature)
            {
                // The preview lists windows of the old snapshot, so it cannot outlive a rebuild.
                CloseRunningPreview();

                _runningSignature = signature;
                RunningItems.ItemsSource = apps;
                RunningSeparator.Visibility = apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            if (RunningItems.ItemsSource is IEnumerable<RunningApp> shown)
            {
                var current = apps.ToDictionary(app => app.ProcessId);
                foreach (var app in shown)
                {
                    app.IsForeground = current.TryGetValue(app.ProcessId, out var fresh) && fresh.IsForeground;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log("RunningApps.Refresh", ex);
        }
    }

    private void Running_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RunningApp app)
        {
            CloseFlyouts();
            RunningApps.Activate(app);
            RefreshRunningApps();
        }
    }

    // ---- the hover preview ----------------------------------------------

    private void Running_MouseEnter(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RunningApp app)
        {
            return;
        }

        if (app.Windows.Count < 2)
        {
            // A single window has nothing to choose between.
            CloseRunningPreview();
            return;
        }

        var alreadyOpen = RunningPopup.IsOpen && ReferenceEquals(_previewApp, app);

        _previewApp = app;
        _previewAnchor = sender as FrameworkElement;
        _previewCloseTimer.Stop();

        if (alreadyOpen)
        {
            return;
        }

        _previewOpenTimer.Stop();
        _previewOpenTimer.Start();
    }

    private void Running_MouseLeave(object sender, MouseEventArgs e)
    {
        _previewOpenTimer.Stop();

        if (RunningPopup.IsOpen)
        {
            _previewCloseTimer.Stop();
            _previewCloseTimer.Start();
        }
    }

    /// <summary>The pointer went from the button onto the card: do not fold the card away.</summary>
    private void RunningPreviewCard_MouseEnter(object sender, MouseEventArgs e)
    {
        _previewOpenTimer.Stop();
        _previewCloseTimer.Stop();
    }

    private void RunningPreviewCard_MouseLeave(object sender, MouseEventArgs e)
    {
        if (RunningPopup.IsOpen)
        {
            _previewCloseTimer.Stop();
            _previewCloseTimer.Start();
        }
    }

    /// <summary>
    /// Raises the card above the bar, one row per window, and starts filling in the still pictures.
    /// </summary>
    private void OpenRunningPreview()
    {
        var app = _previewApp;
        var anchor = _previewAnchor;

        if (app is null || anchor is null || app.Windows.Count < 2)
        {
            CloseRunningPreview();
            return;
        }

        if (app.Windows.Any(window => !NativeMethods.IsWindow(window.Handle)))
        {
            // A window closed between the snapshot and the hover; the next tick rebuilds the buttons.
            CloseRunningPreview();
            return;
        }

        var rows = app.Windows
            .Select(window => new RunningPreview
            {
                ProcessId = app.ProcessId,
                Window = window,
                Icon = app.Icon,
            })
            .ToList();

        // Another flyout would be in the way - and opening one is what a click on the bar does.
        CloseFlyouts();

        RunningPreviewItems.ItemsSource = rows;

        var generation = ++_previewGeneration;

        OpenFlyout(
            RunningPopup,
            PreviewWidth,
            OffsetOf(anchor) + (anchor.ActualWidth / 2) - (PreviewWidth / 2),
            // Flush against the bar, with no gap to cross: every other flyout has one, but here the
            // pointer has to be able to walk straight from the button onto the card.
            0);

        LoadPreviewThumbnails(generation, rows);
    }

    /// <summary>Folds the preview away and makes any capture still running drop its result.</summary>
    private bool CloseRunningPreview()
    {
        _previewOpenTimer.Stop();
        _previewCloseTimer.Stop();
        _previewGeneration++;

        var wasOpen = RunningPopup.IsOpen;
        if (wasOpen)
        {
            RunningPopup.IsOpen = false;
        }

        RunningPreviewItems.ItemsSource = null;
        return wasOpen;
    }

    /// <summary>
    /// Captures each window off the UI thread. PrintWindow waits for the owning application's
    /// thread, so a hung window must not freeze the bar; the rows fill in one by one as their
    /// pictures arrive, and a placeholder icon stands in until then.
    /// </summary>
    private async void LoadPreviewThumbnails(int generation, IReadOnlyList<RunningPreview> rows)
    {
        try
        {
            await Task.WhenAll(rows.Select(row => CapturePreviewAsync(generation, row)));
        }
        catch (Exception ex)
        {
            App.Log("RunningApps.Preview", ex);
        }
    }

    private async Task CapturePreviewAsync(int generation, RunningPreview row)
    {
        var handle = row.Handle;

        var thumbnail = await Task.Run(
            () => WindowPreview.Capture(handle, PreviewCaptureWidth, PreviewCaptureHeight));

        if (generation == _previewGeneration)
        {
            row.Thumbnail = thumbnail;
        }
    }

    private void RunningPreview_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RunningPreview row)
        {
            return;
        }

        e.Handled = true;
        CloseRunningPreview();
        RunningApps.ActivateWindow(row.ProcessId, row.Window);
        RefreshRunningApps();
    }

    private void RunningPreviewClose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RunningPreview row)
        {
            return;
        }

        e.Handled = true;
        RunningApps.Close(row.Window);
        CloseRunningPreview();
    }

    // ---- the button's own menu ------------------------------------------

    private void Running_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RunningApp app)
        {
            return;
        }

        CloseFlyouts();

        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, PlacementTarget = this };

        // Each window of a grouped app stays reachable on its own.
        foreach (var window in app.Windows)
        {
            var entry = new MenuItem
            {
                Header = window.IsMinimized
                    ? $"{ShortenTitle(window.Title)} (minimized)"
                    : ShortenTitle(window.Title),
            };

            var target = window;
            entry.Click += (_, _) => SwitchTo(app, target);
            menu.Items.Add(entry);
        }

        menu.Items.Add(new Separator());

        var minimize = new MenuItem { Header = app.Windows.Count > 1 ? Strings.T("Run_MinimizeAll") : Strings.T("Run_Minimize") };
        minimize.Click += (_, _) => RunningApps.MinimizeAll(app);
        menu.Items.Add(minimize);

        var close = new MenuItem { Header = app.Windows.Count > 1 ? Strings.T("Run_CloseAll") : Strings.T("Run_Close") };
        close.Click += (_, _) => RunningApps.CloseAll(app);
        menu.Items.Add(close);

        menu.IsOpen = true;
        e.Handled = true;
    }

    private static void SwitchTo(RunningApp app, RunningWindow window)
        => RunningApps.ActivateWindow(app.ProcessId, window);

    /// <summary>Window titles can be long; the menu shows them shortened.</summary>
    private static string ShortenTitle(string title)
        => title.Length <= 60 ? title : title[..57] + "…";
}
