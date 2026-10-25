using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DevShell.Interop;

/// <summary>
/// Reserves a strip of screen at the bottom of the monitor the way the real taskbar does,
/// which pushes the system taskbar up instead of fighting it. Registered lazily and always
/// removed on exit; a crash path that skipped ABM_REMOVE would leave the strip reserved
/// until Explorer restarts, which is why this is opt-in rather than the default.
/// </summary>
internal sealed class AppBarDock : IDisposable
{
    private const uint CallbackMessage = 0x8064; // WM_APP + 100
    private const uint ABN_POSCHANGED = 0x0001;

    private readonly Window _window;
    private HwndSource? _source;
    private bool _registered;
    private bool _repositioning;

    public AppBarDock(Window window) => _window = window;

    public bool IsRegistered => _registered;

    /// <summary>Raised with the reserved rectangle in physical pixels.</summary>
    public event Action<NativeMethods.RECT>? Reserved;

    public bool Register(int heightPixels)
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(Hook);

            var data = new NativeMethods.APPBARDATA
            {
                cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
                hWnd = handle,
                uCallbackMessage = CallbackMessage,
                uEdge = NativeMethods.ABE_BOTTOM,
            };

            if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref data) == 0)
            {
                _source?.RemoveHook(Hook);
                _source = null;
                return false;
            }

            _registered = true;
            Apply(heightPixels);
            return true;
        }
        catch (Exception ex)
        {
            App.Log("AppBarDock.Register", ex);
            return false;
        }
    }

    public void Apply(int heightPixels)
    {
        if (!_registered)
        {
            return;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        var monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var screen = info.rcMonitor;
        var data = new NativeMethods.APPBARDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = handle,
            uEdge = NativeMethods.ABE_BOTTOM,
            rc = new NativeMethods.RECT
            {
                Left = screen.Left,
                Top = screen.Bottom - heightPixels,
                Right = screen.Right,
                Bottom = screen.Bottom,
            },
        };

        // QUERYPOS lets the shell avoid other appbars, then we re-assert our height.
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_QUERYPOS, ref data);
        data.rc.Top = data.rc.Bottom - heightPixels;

        if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref data) != 0)
        {
            Reserved?.Invoke(data.rc);
        }
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg != CallbackMessage || _repositioning)
        {
            return IntPtr.Zero;
        }

        if ((uint)lParam.ToInt64() == ABN_POSCHANGED)
        {
            _repositioning = true;
            try
            {
                Apply((int)Math.Round(_window.ActualHeight));
            }
            finally
            {
                _repositioning = false;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            var data = new NativeMethods.APPBARDATA
            {
                cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
                hWnd = new WindowInteropHelper(_window).Handle,
            };

            NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);
        }
        catch (Exception ex)
        {
            App.Log("AppBarDock.Dispose", ex);
        }
        finally
        {
            _registered = false;
            _source?.RemoveHook(Hook);
            _source = null;
        }
    }
}
