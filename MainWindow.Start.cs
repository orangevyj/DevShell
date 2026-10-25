using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DevShell.Interop;
using DevShell.Services;

namespace DevShell;

/// <summary>
/// The Start menu. The rail on the left carries the account, then Settings with the power menu
/// under it; the list on the right is the same set of programs Windows puts in its own menu.
/// </summary>
public partial class MainWindow
{
    private readonly ObservableCollection<StartApp> _startApps = new();
    private bool _startAppsRequested;

    /// <summary>The menu a flyout opened last, so the topmost watch can keep it above that flyout.</summary>
    private ContextMenu? _flyoutMenu;

    private void InitializeStart()
    {
        StartList.ItemsSource = _startApps;

        var account = UserAccount.DisplayName;
        StartHeader.Text = account;
        StartAccountButton.ToolTip = $"Signed in as {account}";

        // Both marks come out of this build, so the tab and the pane look the same on a machine that
        // has never seen the artwork files.
        StartTabInfoIcon.Source = SettingsIcon();
        StartInfoIcon.Source = SettingsIcon();
        StartInfoBanner.Source = EmbeddedImage("pack://application:,,,/Assets/DevShellBanner.png", "Start.Banner");

        ShowStartTab(showInfo: false);

        InitializeSettingsMenu();
    }

    private void OpenStartMenu()
    {
        LoadStartApps();

        // A fresh menu opens on the programs, whatever tab was left showing last time.
        ShowStartTab(showInfo: false);
        ShowFlyoutLeft(StartPopup, 560, StartButton);
    }

    // ---- the DevShell tab -----------------------------------------------

    /// <summary>What the info tab has shown, in the order it showed it, for the copy button.</summary>
    private readonly List<string> _startInfoLines = new();

    private void StartTabApps_Click(object sender, RoutedEventArgs e) => ShowStartTab(showInfo: false);

    private void StartTabInfo_Click(object sender, RoutedEventArgs e) => ShowStartTab(showInfo: true);

    /// <summary>
    /// Switches the right-hand pane between the programs and DevShell's own details. Both bodies of
    /// content live in the same popup, so the menu keeps its size and its place above the bar.
    /// </summary>
    private void ShowStartTab(bool showInfo)
    {
        StartList.Visibility = showInfo ? Visibility.Collapsed : Visibility.Visible;
        StartInfoPanel.Visibility = showInfo ? Visibility.Visible : Visibility.Collapsed;
        StartTabAppsUnderline.Visibility = showInfo ? Visibility.Collapsed : Visibility.Visible;
        StartTabInfoUnderline.Visibility = showInfo ? Visibility.Visible : Visibility.Collapsed;
        StartTabAppsButton.Opacity = showInfo ? 0.6 : 1.0;
        StartTabInfoButton.Opacity = showInfo ? 1.0 : 0.6;

        if (showInfo)
        {
            FillStartInfo();
        }
    }

    /// <summary>
    /// Reads the details fresh every time the tab is shown: the shell mode and the uptime are not
    /// things worth keeping a copy of, and a stale version number would be worse than none.
    /// </summary>
    private void FillStartInfo()
    {
        _startInfoLines.Clear();
        StartInfoRows.Children.Clear();

        var version = InformationalVersion();
        StartInfoVersion.Text = $"v{version}";

        DateTime started = DateTime.Now;
        try
        {
            started = Process.GetCurrentProcess().StartTime;
        }
        catch (Exception ex)
        {
            App.Log("Start.InfoStartTime", ex);
        }

        var uptime = DateTime.Now - started;

        AddStartInfoRow(Strings.T("Info_Version"), version);
        AddStartInfoRow(Strings.T("Info_Assembly"), typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown");
        AddStartInfoRow(Strings.T("Info_Runtime"), RuntimeInformation.FrameworkDescription);
        AddStartInfoRow("Windows", RuntimeInformation.OSDescription);
        AddStartInfoRow(
            Strings.T("Info_Architecture"),
            $"{RuntimeInformation.ProcessArchitecture} process on {RuntimeInformation.OSArchitecture} Windows");
        AddStartInfoRow(Strings.T("Info_Machine"), $"{Environment.MachineName} - {UserAccount.DisplayName}");
        AddStartInfoRow(
            Strings.T("Info_WindowsMode"),
            WindowsIntegration.IsDevShellTheShell() ? Strings.T("Info_ShellMode") : Strings.T("Info_Alongside"));
        AddStartInfoRow(
            Strings.T("Info_Bar"),
            $"{_config.DockMode} - {_config.BarHeight:0} px high - " +
            $"{VisualTreeHelper.GetDpi(this).DpiScaleX * 100:0}% scale");
        AddStartInfoRow(
            Strings.T("Info_Display"),
            $"{SystemParameters.PrimaryScreenWidth:0} x {SystemParameters.PrimaryScreenHeight:0} px");
        AddStartInfoRow(Strings.T("Info_Process"), $"pid {Environment.ProcessId}");
        AddStartInfoRow(Strings.T("Info_RunningSince"), $"{started:g} ({UptimeText(uptime)})");
        AddStartInfoRow(Strings.T("Info_DataFolder"), App.DataDirectory);
    }

    /// <summary>One label/value line of the technical block.</summary>
    private void AddStartInfoRow(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(2, 0, 2, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("CaptionDim"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var text = new TextBlock
        {
            Text = value,
            Style = (Style)FindResource("Caption"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(text, 1);

        row.Children.Add(name);
        row.Children.Add(text);
        StartInfoRows.Children.Add(row);

        _startInfoLines.Add($"{label}: {value}");
    }

    private static string InformationalVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(version))
        {
            // A source-built assembly can carry "+<commit>"; the number is what a user needs.
            var plus = version.IndexOf('+');
            return plus > 0 ? version[..plus] : version;
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }

    private static string UptimeText(TimeSpan up)
    {
        if (up.TotalDays >= 1)
        {
            return $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m";
        }

        return up.TotalHours >= 1
            ? $"{(int)up.TotalHours}h {up.Minutes}m"
            : $"{up.Minutes}m {up.Seconds}s";
    }

    private void StartInfoCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_startInfoLines.Count == 0)
            {
                FillStartInfo();
            }

            Clipboard.SetText(string.Join(Environment.NewLine, _startInfoLines));

            StartInfoCopyButton.Content = Strings.T("Info_Copied");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                StartInfoCopyButton.Content = Strings.T("Info_CopyDetails");
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            App.Log("Start.InfoCopy", ex);
        }
    }

    private void StartInfoFolder_Click(object sender, RoutedEventArgs e) => OpenDataFolder_Click(sender, e);

    /// <summary>
    /// The list is read on a background thread the first time the menu opens: walking two folder
    /// trees and pulling an icon out of every shortcut is far too slow to do at startup.
    /// </summary>
    private void LoadStartApps()
    {
        if (_startAppsRequested)
        {
            return;
        }

        _startAppsRequested = true;

        _ = Task.Run(() =>
        {
            var apps = StartMenuApps.Load();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var app in apps)
                {
                    _startApps.Add(app);
                }

                // The search box's Apps tab may already be open and waiting for this list.
                RefreshAppSearch();
            }));
        });
    }

    private void StartApp_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StartApp app)
        {
            return;
        }

        e.Handled = true;
        CloseFlyouts();
        StartMenuApps.Launch(app);
    }

    private void StartApp_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StartApp app)
        {
            return;
        }

        e.Handled = true;

        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            PlacementTarget = (FrameworkElement)sender,
        };

        PushAboveFlyout(menu);
        AddStartEntry(menu, Strings.T("Hit_Open"), () => StartMenuApps.Launch(app));
        AddStartEntry(menu, Strings.T("Hit_Admin"), () => StartMenuApps.LaunchAsAdministrator(app, out _));
        AddStartEntry(menu, Strings.T("Hit_Location"), () => StartMenuApps.OpenLocation(app));
        menu.IsOpen = true;
    }

    private static void AddStartEntry(ContextMenu menu, string header, Action action)
    {
        var entry = new MenuItem { Header = header };
        entry.Click += (_, _) => action();
        menu.Items.Add(entry);
    }

    // ---- the key on the keyboard ----------------------------------------

    private KeyboardHook? _startKey;

    /// <summary>
    /// Maps the Windows key to the Start button: pressed and let go on its own it opens the same menu
    /// clicking the button does, and pressing it again puts it away, which is how the key behaves in
    /// Windows itself. The combinations Explorer would have answered - Win+R, Win+E, Win+D and the rest
    /// - are in MainWindow.Shortcuts.cs.
    /// </summary>
    private void InitializeStartKey()
    {
        var hook = new KeyboardHook();
        hook.WindowsKeyPressed += OnWindowsKey;
        hook.WindowsKeyChord += OnWindowsKeyChord;

        if (hook.Install())
        {
            _startKey = hook;
            App.LogInfo("Windows key opens the Start menu");
        }

        Closed += (_, _) =>
        {
            _startKey?.Dispose();
            _startKey = null;
        };
    }

    private void OnWindowsKey()
        => Dispatcher.BeginInvoke(new Action(() =>
        {
            // A fullscreen app hides the bar, and the key is the way back to it: Windows' own key
            // brings its taskbar up over a fullscreen app in exactly the same way.
            BringBarBackForUser();

            // The key is not a click, so nothing has handed the bar the foreground by itself. Without
            // this the flyout opens as a window of a background process and stays behind whatever the
            // user was working in - which is exactly what "the Windows key does nothing" looks like.
            BringBarToFront();

            if (StartPopup.IsOpen)
            {
                CloseFlyouts();
                return;
            }

            OpenStartMenu();
        }));

    // ---- the Start button's right-click menu ----------------------------

    /// <summary>
    /// Right-clicking the Start button puts up the menu Windows puts there: the power-user entries
    /// built out of the WinX shortcut folders - Device Manager, System, Task Manager and the rest -
    /// with the shell's own names and icons for them. Left-clicking the button still opens the Start
    /// menu, and the bar's own menu stays on the rest of the bar.
    /// </summary>
    private void Start_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowStartButtonMenu(PlacementMode.MousePoint);
    }

    /// <summary>
    /// Puts up the Start button's menu: at the pointer, which is where a right-click asks for it, or
    /// above the button itself, which is where Win+X asks for it.
    /// </summary>
    private void ShowStartButtonMenu(PlacementMode placement)
    {
        var menu = new ContextMenu
        {
            Placement = placement,
            PlacementTarget = StartButton,
        };

        PushAboveFlyout(menu);
        AddStartButtonEntries(menu);
        menu.IsOpen = true;

        // Worth a line in the log: an empty menu still means the hook fired.
        App.LogInfo($"Start button menu: {menu.Items.Count} entries");
    }

    /// <summary>Fills the Start button's menu, with Windows' own separators between its groups.</summary>
    private static void AddStartButtonEntries(ContextMenu menu)
    {
        var groups = StartButtonMenu.Load();

        foreach (var group in groups)
        {
            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new Separator());
            }

            foreach (var item in group)
            {
                var entry = new MenuItem { Header = item.Name, ToolTip = item.ToolTip };

                if (item.Icon is not null)
                {
                    entry.Icon = new Image { Source = item.Icon, Width = 16, Height = 16 };
                }

                var target = item;
                entry.Click += (_, _) => StartButtonMenu.Launch(target);
                menu.Items.Add(entry);
            }
        }

        if (menu.Items.Count == 0)
        {
            // A profile with no WinX shortcut folders at all: say so rather than open an empty box.
            menu.Items.Add(new MenuItem { Header = Strings.T("Start_NothingToShow"), IsEnabled = false });
        }
    }

    /// <summary>The rail's account button: its tooltip names the account, and clicking opens the page about it.</summary>
    private void StartAccount_Click(object sender, RoutedEventArgs e)
    {
        CloseFlyouts();
        ShellActions.Open("ms-settings:yourinfo");
    }

    /// <summary>Windows' own Settings app, which is what the gear glyph means everywhere else.</summary>
    private void StartSettings_Click(object sender, RoutedEventArgs e)
    {
        CloseFlyouts();
        ShellActions.Open("ms-settings:");
    }

    private void StartPower_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            Placement = PlacementMode.Top,
            PlacementTarget = StartPowerButton,
        };

        PushAboveFlyout(menu);
        AddStartEntry(menu, Strings.T("Power_Sleep"), () => PowerActions.Sleep());
        AddStartEntry(menu, Strings.T("Power_Restart"), () => PowerActions.Restart());
        AddStartEntry(menu, Strings.T("Power_ShutDown"), () => PowerActions.ShutDown());
        menu.Items.Add(new Separator());
        AddStartEntry(menu, Strings.T("Power_SignOut"), () => PowerActions.SignOut());
        menu.IsOpen = true;
    }

    /// <summary>
    /// A flyout is a topmost window of its own, so a menu opened while one is up has to be pushed
    /// above it - and pushed again on every tick of the topmost watch, which raises the flyout.
    /// </summary>
    private void PushAboveFlyout(ContextMenu menu)
    {
        _flyoutMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_flyoutMenu, menu))
            {
                _flyoutMenu = null;
            }
        };

        menu.Opened += (_, _) => menu.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => WindowEffects.AssertTopmost(menu)));
    }
}
