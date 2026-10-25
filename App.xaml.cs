using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace DevShell;

public partial class App : Application
{
    private const string MutexName = @"Global\DevShell.SingleInstance";
    private Mutex? _instanceMutex;

    /// <summary>The desktop surface, when it is switched on. Owned here so it outlives nothing else.</summary>
    private DesktopWindow? _desktop;

    /// <summary>The desktop surface, for the bar's settings flyout to push its changes at.</summary>
    internal DesktopWindow? DesktopSurface => _desktop;

    internal static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevShell");

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        Directory.CreateDirectory(DataDirectory);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("Task", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);

        // Before any window is read: the XAML pulls its text from the table as it loads.
        Services.Strings.Apply(Services.Config.Load().Language);

        // Explorer is what hosts packaged (UWP/Store) app activation, so a machine that starts
        // DevShell instead of Explorer has to have that host started for it - see ShellAppRuntime.
        if (Services.WindowsIntegration.IsDevShellTheShell())
        {
            Interop.ShellAppRuntime.EnsureRunning();
        }

        // The desktop goes up first so the bar is drawn over it rather than the other way round.
        ShowDesktopLayer();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Shows the desktop surface if the settings want it, or tears it down if they no longer do.
    /// Called at startup and whenever the bar's menu toggles the layer.
    /// </summary>
    internal void ShowDesktopLayer()
    {
        try
        {
            if (Services.Config.Load().ShowDesktopLayer)
            {
                if (_desktop is null)
                {
                    _desktop = new DesktopWindow();
                    _desktop.Closed += (_, _) => _desktop = null;
                    _desktop.Show();
                }

                return;
            }

            _desktop?.Close();
            _desktop = null;
        }
        catch (Exception ex)
        {
            Log("ShowDesktopLayer", ex);
            _desktop = null;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Dispatcher", e.Exception);
        e.Handled = true;
    }

    internal static void Log(string source, Exception? ex)
    {
        if (ex is null)
        {
            return;
        }

        LogInfo($"{source}: {ex}");
    }

    /// <summary>Plain informational line, e.g. index statistics.</summary>
    internal static void LogInfo(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(DataDirectory, "log.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // never let logging take the bar down
        }
    }
}
