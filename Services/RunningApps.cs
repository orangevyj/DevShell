using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DevShell.Interop;

namespace DevShell.Services;

/// <summary>One window of a running application, as the bar lists it.</summary>
internal sealed record RunningWindow(IntPtr Handle, string Title, bool IsMinimized);

/// <summary>
/// An application that has real windows on screen, grouped by process the way the Windows 11
/// taskbar groups them. Only windows that are actually on screen get here: a background process
/// with no window, or a suspended UWP app whose window is cloaked, never shows up.
/// </summary>
internal sealed class RunningApp : INotifyPropertyChanged
{
    private bool _isForeground;

    public required uint ProcessId { get; init; }

    public required string Name { get; init; }

    public required ImageSource? Icon { get; init; }

    /// <summary>In z-order, so the first window is the one the user last worked in.</summary>
    public required IReadOnlyList<RunningWindow> Windows { get; init; }

    /// <summary>True for an application with more than one window: its button shows a stack.</summary>
    public bool IsGrouped => Windows.Count > 1;

    /// <summary>Three or more windows put a second silhouette behind the first.</summary>
    public bool HasManyWindows => Windows.Count > 2;

    /// <summary>True while one of the app's windows is the foreground window.</summary>
    public bool IsForeground
    {
        get => _isForeground;
        set
        {
            if (_isForeground == value)
            {
                return;
            }

            _isForeground = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsForeground)));
        }
    }

    /// <summary>
    /// The plain tooltip is for a single window only: a grouped button opens the window preview
    /// instead, and a tooltip sliding in over it would only cover it up.
    /// </summary>
    public string? ToolTip => Windows.Count == 1 ? Windows[0].Title : null;

    /// <summary>Changes only when the set of windows does, so the bar can skip pointless rebuilds.</summary>
    public string Signature
        => $"{ProcessId}:{string.Join('|', Windows.Select(window => window.Handle.ToInt64()))}";

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One row of the hover preview: a window of a grouped app, with a still picture that is filled in
/// once the capture made off the UI thread comes back.
/// </summary>
internal sealed class RunningPreview : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;

    public required uint ProcessId { get; init; }

    public required RunningWindow Window { get; init; }

    public required ImageSource? Icon { get; init; }

    public IntPtr Handle => Window.Handle;

    public string Title => Window.Title;

    /// <summary>Only a minimized window has something to say; otherwise the title says it all.</summary>
    public string State => Window.IsMinimized ? "Minimized" : string.Empty;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value))
            {
                return;
            }

            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThumbnail)));
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The applications with windows on screen, read straight from the window manager rather than
/// from the process list - that is what keeps windowless background processes out of the bar.
/// </summary>
internal static class RunningApps
{
    /// <summary>
    /// Where each application was last worked in, per process. A grouped button raises this window
    /// rather than whichever one the enumeration happens to list first - which, for minimized
    /// windows, is not a meaningful order at all.
    /// </summary>
    private static readonly Dictionary<uint, IntPtr> LastActive = [];

    /// <summary>Window classes that are the shell's own furniture rather than an application.</summary>
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Shell_InputSwitchTopLevelWindow",
        "Windows.UI.Core.CoreWindow",
    };

    public static IReadOnlyList<RunningApp> Snapshot()
    {
        var byProcess = new Dictionary<uint, List<RunningWindow>>();
        var self = (uint)Environment.ProcessId;

        try
        {
            NativeMethods.EnumWindows((window, _) =>
            {
                if (!IsApplicationWindow(window, self, out var processId))
                {
                    return true;
                }

                if (!byProcess.TryGetValue(processId, out var windows))
                {
                    byProcess[processId] = windows = [];
                }

                windows.Add(new RunningWindow(
                    window, TitleOf(window), NativeMethods.IsIconic(window)));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            App.Log("RunningApps.Snapshot", ex);
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var apps = new List<RunningApp>(byProcess.Count);

        foreach (var (processId, windows) in byProcess)
        {
            if (windows.Any(window => window.Handle == foreground))
            {
                LastActive[processId] = foreground;
            }

            // Record the window the user is working in; PreferredWindow() hands it back to a click.
            apps.Add(new RunningApp
            {
                ProcessId = processId,
                Name = windows[0].Title,
                Icon = IconFor(windows[0].Handle, processId),
                Windows = windows,
                IsForeground = windows.Any(window => window.Handle == foreground),
            });
        }

        // Processes that are gone take their remembered window with them.
        foreach (var stale in LastActive.Keys.Where(id => !byProcess.ContainsKey(id)).ToList())
        {
            LastActive.Remove(stale);
        }

        // Oldest first, so buttons stay put instead of following the ever-changing z-order.
        return apps
            .OrderBy(StartedAt)
            .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static string SignatureOf(IEnumerable<RunningApp> apps)
        => string.Join(';', apps.Select(app => app.Signature));

    /// <summary>
    /// A click on a button: switch to the app, step through its windows when it is already in
    /// front, or fold a single window away - the same toggle the taskbar performs.
    /// </summary>
    public static void Activate(RunningApp app)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var active = -1;

        for (var index = 0; index < app.Windows.Count; index++)
        {
            if (app.Windows[index].Handle == foreground)
            {
                active = index;
                break;
            }
        }

        // A click on the bar gives the bar itself the foreground, so the app's own marker - read
        // from the last tick - is what says whether the user was already working in it.
        if (active >= 0 || app.IsForeground)
        {
            if (app.Windows.Count == 1)
            {
                WindowActivation.Minimize(app.Windows[0].Handle);
                return;
            }

            if (active < 0)
            {
                for (var index = 0; index < app.Windows.Count; index++)
                {
                    if (app.Windows[index].Handle == PreferredWindow(app))
                    {
                        active = index;
                        break;
                    }
                }
            }

            // Already in front: step to the next window, the way a grouped taskbar button does,
            // instead of raising the one that is on top already.
            ActivateWindow(app.ProcessId, app.Windows[(Math.Max(active, 0) + 1) % app.Windows.Count]);
            return;
        }

        // Not in front: the window the user last worked in, which the z-order cannot tell us once
        // the windows are minimized.
        var target = app.Windows.FirstOrDefault(window => window.Handle == PreferredWindow(app))
                     ?? app.Windows[0];

        ActivateWindow(app.ProcessId, target);
    }

    /// <summary>
    /// The window a click on the button should raise: the one the user last worked in, falling
    /// back to the top of the z-order when that window is gone.
    /// </summary>
    public static IntPtr PreferredWindow(RunningApp app)
        => LastActive.TryGetValue(app.ProcessId, out var remembered)
           && app.Windows.Any(window => window.Handle == remembered)
            ? remembered
            : app.Windows[0].Handle;

    /// <summary>Raises one particular window and remembers it as the app's last active one.</summary>
    public static void ActivateWindow(uint processId, RunningWindow window)
    {
        LastActive[processId] = window.Handle;

        if (!WindowActivation.BringToFront(window.Handle))
        {
            App.LogInfo($"RunningApps: window '{window.Title}' did not come to the front");
        }
    }

    /// <summary>Folds every window of the app away, the taskbar's own "Minimize all".</summary>
    public static void MinimizeAll(RunningApp app)
    {
        foreach (var window in app.Windows)
        {
            WindowActivation.Minimize(window.Handle);
        }
    }

    public static void Close(RunningWindow window) => WindowActivation.Close(window.Handle);

    public static void CloseAll(RunningApp app)
    {
        foreach (var window in app.Windows)
        {
            WindowActivation.Close(window.Handle);
        }
    }

    private static bool IsApplicationWindow(IntPtr window, uint selfProcessId, out uint processId)
    {
        processId = 0;

        if (!NativeMethods.IsWindowVisible(window) || NativeMethods.GetWindowTextLength(window) == 0)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out processId);
        if (processId == 0 || processId == selfProcessId)
        {
            // The bar and the desktop layer are the shell itself, not one of its tasks.
            return false;
        }

        var exStyle = (uint)NativeMethods.GetWindowLong(window, NativeMethods.GWL_EXSTYLE);

        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0
            && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
        {
            return false;
        }

        // Owned windows (dialogs, palettes) belong to their owner's button unless they ask to be
        // listed in their own right.
        if (NativeMethods.GetWindow(window, NativeMethods.GW_OWNER) != IntPtr.Zero
            && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
        {
            return false;
        }

        if (IsCloaked(window))
        {
            // A window that exists but is not on screen: suspended UWP apps, other virtual desktops.
            return false;
        }

        return !IgnoredClasses.Contains(ClassNameOf(window));
    }

    private static bool IsCloaked(IntPtr window)
    {
        try
        {
            return NativeMethods.DwmGetWindowAttribute(
                       window, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
                   && cloaked != 0;
        }
        catch (Exception ex)
        {
            App.Log("RunningApps.IsCloaked", ex);
            return false;
        }
    }

    private static string ClassNameOf(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        var length = NativeMethods.GetClassName(window, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    private static string TitleOf(IntPtr window)
    {
        var length = NativeMethods.GetWindowTextLength(window);
        var buffer = new StringBuilder(length + 2);
        var copied = NativeMethods.GetWindowText(window, buffer, buffer.Capacity);
        return copied > 0 ? buffer.ToString(0, copied) : string.Empty;
    }

    private static ImageSource? IconFor(IntPtr window, uint processId)
    {
        foreach (var handle in IconHandles(window))
        {
            var source = FromHandle(handle);
            if (source is not null)
            {
                return source;
            }
        }

        // Falling back to the executable's own icon covers windows that never set one.
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var path = process.MainModule?.FileName;
            return string.IsNullOrEmpty(path) ? null : ShellIcons.ForPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<IntPtr> IconHandles(IntPtr window)
    {
        var big = QueryIcon(window, NativeMethods.ICON_BIG);
        if (big != IntPtr.Zero)
        {
            yield return big;
        }

        var small = QueryIcon(window, NativeMethods.ICON_SMALL2);
        if (small != IntPtr.Zero)
        {
            yield return small;
        }

        foreach (var index in new[] { NativeMethods.GCLP_HICON, NativeMethods.GCLP_HICONSM })
        {
            IntPtr fromClass;
            try
            {
                fromClass = NativeMethods.GetClassLongPtr(window, index);
            }
            catch (EntryPointNotFoundException)
            {
                // 32-bit builds only have GetClassLong; the other sources still work.
                yield break;
            }

            if (fromClass != IntPtr.Zero)
            {
                yield return fromClass;
            }
        }
    }

    private static IntPtr QueryIcon(IntPtr window, int which)
    {
        var sent = NativeMethods.SendMessageTimeout(
            window, (uint)NativeMethods.WM_GETICON, new IntPtr(which), IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, 200, out var result);

        return sent == IntPtr.Zero ? IntPtr.Zero : result;
    }

    private static ImageSource? FromHandle(IntPtr icon)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            App.Log("RunningApps.FromHandle", ex);
            return null;
        }
    }

    private static DateTime StartedAt(RunningApp app)
    {
        try
        {
            using var process = Process.GetProcessById((int)app.ProcessId);
            return process.StartTime;
        }
        catch
        {
            // A process we are not allowed to open: leave it at the end rather than guessing.
            return DateTime.MaxValue;
        }
    }
}
