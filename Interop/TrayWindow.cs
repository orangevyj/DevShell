using System.Runtime.InteropServices;

namespace DevShell.Interop;

/// <summary>
/// One notification an application handed to the notification area through Shell_NotifyIcon.
/// </summary>
internal readonly record struct TrayNotification(
    uint Command,
    IntPtr Owner,
    uint Id,
    uint Flags,
    uint Callback,
    IntPtr Icon,
    string Tip,
    bool Hidden,
    uint Version);

/// <summary>
/// The window Shell_NotifyIcon looks for. Applications publish notification-area icons by sending
/// WM_COPYDATA to the window of class "Shell_TrayWnd"; with DevShell acting as the shell there is no
/// Explorer taskbar, so DevShell has to own that window - otherwise every background application is
/// silently invisible.
///
/// The window is never shown. It exists only to receive the notifications, so the bar can draw the
/// icons itself and forward clicks back to their owners.
/// </summary>
internal static class TrayWindow
{
    // Shell_NotifyIcon's command, carried in COPYDATASTRUCT.dwData.
    internal const uint NIM_ADD = 0;
    internal const uint NIM_MODIFY = 1;
    internal const uint NIM_DELETE = 2;
    internal const uint NIM_SETFOCUS = 3;
    internal const uint NIM_SETVERSION = 4;

    // NOTIFYICONDATA.uFlags bits that say which of the other fields are valid.
    internal const uint NIF_MESSAGE = 0x00000001;
    internal const uint NIF_ICON = 0x00000002;
    internal const uint NIF_TIP = 0x00000004;
    internal const uint NIF_STATE = 0x00000008;

    internal const uint NIS_HIDDEN = 0x00000001;

    /// <summary>NIN_SELECT: what a NOTIFYICON_VERSION_4 icon reports a left click as.</summary>
    internal const uint NIN_SELECT = 0x0400;

    /// <summary>Pre-v4 icons report clicks as mouse messages in the low word of lParam.</summary>
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_RBUTTONUP = 0x0205;

    internal const uint WM_CONTEXTMENU = 0x007B;

    private const string ClassName = "Shell_TrayWnd";
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_CLOSE = 0x0010;
    private const uint WS_POPUP = 0x80000000;
    private const int InfoChars = 256;

    /// <summary>sizeof(NOTIFYICONDATAW) on 64-bit; the largest notification Windows 10 sends.</summary>
    private const int MaximumSize = 2048;

    /// <summary>
    /// Windows 11 does not send a bare NOTIFYICONDATA any more. Shell_NotifyIcon wraps it in a packet
    /// that starts with this signature and carries the NIM command in the second dword; the structure
    /// behind it is the 32-bit layout, and the owner's executable path follows it. Both shapes are
    /// accepted, so the host also works on Windows 10.
    /// </summary>
    private const uint PacketSignature = 0x34753423;
    private const int PacketHeader = 8;

    private static readonly WindowProcedure Procedure = HandleMessage;
    private static Action<TrayNotification>? _handler;
    private static IntPtr _handle;
    private static IntPtr _module;

    /// <summary>The host window, or zero when nothing is hosting the notification area.</summary>
    internal static IntPtr Handle => _handle;

    /// <summary>
    /// Creates the host window and starts delivering <paramref name="handler"/> every notification.
    /// Returns false when the class cannot be registered, which in practice means another shell
    /// already owns the name.
    /// </summary>
    internal static bool Attach(Action<TrayNotification> handler)
    {
        if (_handle != IntPtr.Zero)
        {
            return true;
        }

        _handler = handler;
        _module = GetModuleHandle(null);

        var windowClass = new WindowClass
        {
            cbSize = (uint)Marshal.SizeOf<WindowClass>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Procedure),
            hInstance = _module,
            lpszClassName = ClassName,
        };

        if (RegisterClassEx(ref windowClass) == 0)
        {
            App.LogInfo($"Tray: RegisterClassEx failed ({Marshal.GetLastWin32Error()})");
            return false;
        }

        _handle = CreateWindowEx(0, ClassName, "DevShell notification area", WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _module, IntPtr.Zero);

        if (_handle == IntPtr.Zero)
        {
            App.LogInfo($"Tray: CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Explorer broadcasts TaskbarCreated after it rebuilds the taskbar; applications listen for it and
    /// register their icons again. Sending it turns the icons of programs that started before the bar
    /// into visible ones instead of leaving them unregistered.
    /// </summary>
    internal static void BroadcastTaskbarCreated()
    {
        var message = RegisterWindowMessage("TaskbarCreated");
        if (message != 0)
        {
            PostMessage(new IntPtr(0xFFFF), message, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static IntPtr HandleMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_COPYDATA)
        {
            if (_handler is not null && Parse(lParam) is { } notification)
            {
                _handler(notification);
            }

            // WM_COPYDATA answers TRUE: Shell_NotifyIcon reports that as success.
            return new IntPtr(1);
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private static TrayNotification? Parse(IntPtr copyData)
    {
        if (copyData == IntPtr.Zero)
        {
            return null;
        }

        var data = Marshal.PtrToStructure<CopyData>(copyData);
        if (data.lpData == IntPtr.Zero || data.cbData < 16)
        {
            return null;
        }

        var size = (int)Math.Min(data.cbData, (uint)MaximumSize);
        var buffer = Marshal.AllocHGlobal(MaximumSize);
        try
        {
            // Zeroed first: a 32-bit sender's structure is shorter than ours, and a field outside it
            // must read as empty rather than as whatever the allocator had there.
            for (var offset = 0; offset < MaximumSize; offset += 8)
            {
                Marshal.WriteInt64(buffer, offset, 0);
            }

            CopyMemory(buffer, data.lpData, (UIntPtr)size);

            // Windows 11 prefixes the structure with its own packet header; Windows 10 sends the
            // structure alone and puts the command in the COPYDATASTRUCT instead.
            var packet = size >= PacketHeader + 16 &&
                         (uint)Marshal.ReadInt32(buffer, 0) == PacketSignature;
            var body = packet ? PacketHeader : 0;
            var command = packet
                ? (uint)Marshal.ReadInt32(buffer, 4)
                : (uint)data.dwData.ToInt64();

            var cbSize = (uint)Marshal.ReadInt32(buffer, body);
            var x86 = IsThirtyTwoBit(buffer, body, size, cbSize);

            var hwndOffset = body + (x86 ? 4 : 8);
            var idOffset = body + (x86 ? 8 : 16);
            var flagsOffset = body + (x86 ? 12 : 20);
            var callbackOffset = body + (x86 ? 16 : 24);
            var iconOffset = body + (x86 ? 20 : 32);
            var tipOffset = body + (x86 ? 24 : 40);
            var tipChars = Classic(cbSize, x86) ? 64 : 128;

            var stateOffset = tipOffset + tipChars * 2;
            var versionOffset = stateOffset + 8 + InfoChars * 2;

            var hidden = false;
            if (size >= stateOffset + 8)
            {
                var state = (uint)Marshal.ReadInt32(buffer, stateOffset);
                var mask = (uint)Marshal.ReadInt32(buffer, stateOffset + 4);
                hidden = (state & mask & NIS_HIDDEN) != 0;
            }

            var version = size >= versionOffset + 4
                ? (uint)Marshal.ReadInt32(buffer, versionOffset)
                : 0u;

            // A 32-bit structure stores handles as four bytes; an eight-byte load would pick up the
            // following field in the high half.
            IntPtr Handle(int offset) => x86
                ? (IntPtr)Marshal.ReadInt32(buffer, offset)
                : Marshal.ReadIntPtr(buffer, offset);

            return new TrayNotification(
                command,
                Handle(hwndOffset),
                (uint)Marshal.ReadInt32(buffer, idOffset),
                (uint)Marshal.ReadInt32(buffer, flagsOffset),
                (uint)Marshal.ReadInt32(buffer, callbackOffset),
                Handle(iconOffset),
                ReadText(buffer, size, tipOffset, tipChars),
                hidden,
                version);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Whether the sender built a 32-bit NOTIFYICONDATAW. The structure is laid out with native
    /// pointers, so cbSize alone identifies most layouts; 952 is both a 32-bit v3 and a 64-bit v2
    /// structure, and there the window handle has to decide.
    /// </summary>
    private static bool IsThirtyTwoBit(IntPtr buffer, int body, int size, uint cbSize)
    {
        switch (cbSize)
        {
            case 152:
            case 936:
            case 956:
                return true;
            case 168:
            case 968:
            case 976:
                return false;
            case 952:
                return !IsWindow(Marshal.ReadIntPtr(buffer, 8));
            default:
                // Something we do not know the shape of; every Windows since Vista sends v4 (976)
                // when it can, so assume the 64-bit layout.
                return size <= 152;
        }
    }

    /// <summary>The pre-2000 structure only had room for 64 characters of tooltip.</summary>
    private static bool Classic(uint cbSize, bool x86) => cbSize == (x86 ? 152u : 168u);

    private static string ReadText(IntPtr buffer, int size, int offset, int maxChars)
    {
        var available = (size - offset) / 2;
        if (available <= 0)
        {
            return string.Empty;
        }

        var text = Marshal.PtrToStringUni(buffer + offset, Math.Min(maxChars, available)) ?? string.Empty;
        var end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).Trim();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData
    {
        // ULONG_PTR: eight bytes on 64-bit, which is what makes the field offsets below line up.
        public IntPtr dwData;
        public uint cbData;
        public IntPtr lpData;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr destination, IntPtr source, UIntPtr length);
}
