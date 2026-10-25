using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DevShell.Interop;

namespace DevShell.Services;

/// <summary>One background application's icon in the notification area.</summary>
internal sealed class TrayIcon : INotifyPropertyChanged
{
    private ImageSource? _image;
    private string _tip = string.Empty;
    private bool _hidden;

    internal TrayIcon(IntPtr owner, uint id)
    {
        Owner = owner;
        Id = id;
    }

    /// <summary>The window the application wants its notifications delivered to.</summary>
    internal IntPtr Owner { get; }

    internal uint Id { get; }

    internal uint CallbackMessage { get; set; }

    /// <summary>NOTIFYICON_VERSION the icon registered with; 4 changes how clicks are reported.</summary>
    internal uint Version { get; set; }

    /// <summary>True when the application asked for the icon to be hidden from the visible tray.</summary>
    internal bool Hidden
    {
        get => _hidden;
        set => Set(ref _hidden, value);
    }

    public string Tip
    {
        get => _tip;
        set
        {
            if (!Set(ref _tip, value))
            {
                return;
            }

            Raise(nameof(Label));
        }
    }

    public ImageSource? Icon
    {
        get => _image;
        set => Set(ref _image, value);
    }

    /// <summary>What the icon reads as in the flyout and its tooltip.</summary>
    public string Label => string.IsNullOrWhiteSpace(Tip) ? "Background app" : Tip;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// DevShell's side of the notification area: the window Shell_NotifyIcon talks to, the icons it has
/// been told about, and the click forwarding that lets those icons behave as they do under Explorer.
/// </summary>
internal static class NotificationArea
{
    private static readonly List<TrayIcon> Items = [];

    /// <summary>
    /// Versions can arrive before the icon they belong to, so they are kept until the NIM_ADD lands.
    /// </summary>
    private static readonly Dictionary<(IntPtr Owner, uint Id), uint> PendingVersions = [];

    /// <summary>Everything currently registered, in the order the applications registered it.</summary>
    public static ObservableCollection<TrayIcon> Icons { get; } = [];

    /// <summary>Whether DevShell owns the notification area in this session.</summary>
    public static bool IsHosting { get; private set; }

    /// <summary>Raised whenever the set of icons or one of their icons/tooltips changes.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Starts hosting the notification area, unless Explorer (or another shell) already does.
    /// </summary>
    public static void Start()
    {
        if (IsHosting)
        {
            return;
        }

        if (!WindowsIntegration.IsDevShellTheShell())
        {
            App.LogInfo("Tray: not the shell, so the notification area stays with Explorer");
            return;
        }

        var existing = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (existing != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(existing, out var owner);
            if (owner != (uint)Environment.ProcessId)
            {
                App.LogInfo("Tray: another shell already owns the notification area");
                return;
            }
        }

        if (!TrayWindow.Attach(OnNotification))
        {
            App.LogInfo("Tray: could not create the Shell_TrayWnd host");
            return;
        }

        IsHosting = true;

        // Programs that were already running registered their icons with a notification area that
        // did not exist; this is the same broadcast Explorer sends when it rebuilds its taskbar.
        TrayWindow.BroadcastTaskbarCreated();

        App.LogInfo("Tray: hosting the notification area");
    }

    /// <summary>Left click: ask the application to activate itself, as Explorer does.</summary>
    public static void Activate(TrayIcon icon) => Notify(icon, TrayWindow.NIN_SELECT, TrayWindow.WM_LBUTTONUP);

    /// <summary>Right click: let the application show its own menu, as Explorer does.</summary>
    public static void ShowMenu(TrayIcon icon) => Notify(icon, TrayWindow.WM_CONTEXTMENU, TrayWindow.WM_RBUTTONUP);

    private static void Notify(TrayIcon icon, uint version4Event, uint classicEvent)
    {
        if (icon.Owner == IntPtr.Zero || icon.CallbackMessage == 0)
        {
            return;
        }

        NativeMethods.GetCursorPos(out var point);

        IntPtr wParam;
        IntPtr lParam;
        if (icon.Version >= 4)
        {
            // NOTIFYICON_VERSION_4: wParam carries the anchor point, lParam the event and the id.
            wParam = Make(point.X, point.Y);
            lParam = Make((int)(version4Event & 0xFFFF), (int)(icon.Id & 0xFFFF));
        }
        else
        {
            // The classic contract: wParam is the id, lParam the mouse message.
            wParam = (IntPtr)(int)icon.Id;
            lParam = (IntPtr)(int)classicEvent;
        }

        NativeMethods.PostMessage(icon.Owner, icon.CallbackMessage, wParam, lParam);
    }

    private static IntPtr Make(int low, int high) =>
        (IntPtr)(((high & 0xFFFF) << 16) | (low & 0xFFFF));

    private static void OnNotification(TrayNotification notification)
    {
        switch (notification.Command)
        {
            case TrayWindow.NIM_ADD:
            case TrayWindow.NIM_MODIFY:
                Apply(notification, adding: notification.Command == TrayWindow.NIM_ADD);
                break;
            case TrayWindow.NIM_DELETE:
                Remove(notification);
                break;
            case TrayWindow.NIM_SETVERSION:
                RememberVersion(notification);
                break;
        }
    }

    private static void Apply(TrayNotification notification, bool adding)
    {
        var icon = Find(notification.Owner, notification.Id);
        if (icon is null)
        {
            if (!adding)
            {
                return;
            }

            icon = new TrayIcon(notification.Owner, notification.Id);
            if (PendingVersions.Remove((notification.Owner, notification.Id), out var version))
            {
                icon.Version = version;
            }

            Items.Add(icon);
            Icons.Add(icon);
        }

        if ((notification.Flags & TrayWindow.NIF_MESSAGE) != 0)
        {
            icon.CallbackMessage = notification.Callback;
        }

        if ((notification.Flags & TrayWindow.NIF_ICON) != 0)
        {
            icon.Icon = Resolve(notification.Icon, notification.Owner) ?? icon.Icon;
        }

        if ((notification.Flags & TrayWindow.NIF_TIP) != 0)
        {
            icon.Tip = notification.Tip;
        }

        if ((notification.Flags & TrayWindow.NIF_STATE) != 0)
        {
            icon.Hidden = notification.Hidden;
        }

        // Some applications add an icon without ever supplying one and rely on their executable's
        // icon, so an entry that still has none borrows the icon of the process behind it.
        icon.Icon ??= ProcessIcon(notification.Owner);

        Changed?.Invoke();
    }

    private static void Remove(TrayNotification notification)
    {
        if (Find(notification.Owner, notification.Id) is not { } icon)
        {
            return;
        }

        Items.Remove(icon);
        Icons.Remove(icon);
        PendingVersions.Remove((notification.Owner, notification.Id));
        Changed?.Invoke();
    }

    private static void RememberVersion(TrayNotification notification)
    {
        if (Find(notification.Owner, notification.Id) is { } icon)
        {
            icon.Version = notification.Version;
            return;
        }

        PendingVersions[(notification.Owner, notification.Id)] = notification.Version;
    }

    private static TrayIcon? Find(IntPtr owner, uint id) =>
        Items.FirstOrDefault(icon => icon.Owner == owner && icon.Id == id);

    /// <summary>
    /// Converts the icon handle the application handed over. The handle belongs to the other process,
    /// so when Windows refuses it the icon of the owning executable stands in.
    /// </summary>
    private static ImageSource? Resolve(IntPtr handle, IntPtr owner)
    {
        if (handle != IntPtr.Zero)
        {
            try
            {
                var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                App.Log("Tray.Icon", ex);
            }
        }

        return ProcessIcon(owner);
    }

    private static ImageSource? ProcessIcon(IntPtr owner)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(owner, out var processId);
            if (processId == 0)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)processId);
            var path = process.MainModule?.FileName;
            return string.IsNullOrEmpty(path) ? null : ShellIcons.ForPath(path);
        }
        catch (Exception ex)
        {
            App.Log("Tray.ProcessIcon", ex);
            return null;
        }
    }
}
