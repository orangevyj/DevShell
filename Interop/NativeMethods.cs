using System.Runtime.InteropServices;
using System.Text;

namespace DevShell.Interop;

internal static class NativeMethods
{
    // ---- window placement / z-order -------------------------------------
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal static readonly IntPtr HWND_BOTTOM = new(1);
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_NOOWNERZORDER = 0x0200;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>GW_HWNDNEXT: the window one step lower in the z-order.</summary>
    internal const uint GW_HWNDNEXT = 2;

    /// <summary>
    /// The shell's desktop window (Progman). The desktop layer is parked directly above it,
    /// so it stays behind every ordinary window while still covering Explorer's icons.
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// The window's own rectangle, which for a window DWM draws includes the invisible resize border
    /// on the outside of the frame. <see cref="DWMWA_EXTENDED_FRAME_BOUNDS"/> is what the user sees.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    // ---- monitors -------------------------------------------------------
    internal const int MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hWnd, int flags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // ---- appbar (reserve screen space) ---------------------------------
    internal const uint ABM_NEW = 0x00;
    internal const uint ABM_REMOVE = 0x01;
    internal const uint ABM_QUERYPOS = 0x02;
    internal const uint ABM_SETPOS = 0x03;
    internal const uint ABE_BOTTOM = 0x03;

    [StructLayout(LayoutKind.Sequential)]
    internal struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    internal static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    // ---- comctl32: window composition (acrylic / blur) -----------------
    internal const int WCA_ACCENT_POLICY = 19;

    internal enum AccentState
    {
        Disabled = 0,

        /// <summary>Translucent tint without any blur - the backdrop a see-through bar falls back to.</summary>
        TransparentGradient = 2,
        EnableBlurBehind = 3,
        EnableAcrylicBlurBehind = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowCompositionAttribute(IntPtr hWnd, ref WindowCompositionAttributeData data);

    // ---- shell: icons ---------------------------------------------------
    internal const uint SHGFI_ICON = 0x00000100;
    internal const uint SHGFI_LARGEICON = 0x00000000;
    internal const uint SHGFI_SMALLICON = 0x00000001;
    internal const uint SHGFI_USEFILEATTRIBUTES = 0x00000010;
    internal const uint SHGFI_SYSICONINDEX = 0x00004000;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    // The desktop draws 48px icons, which is the shell's "extra large" image list rather than the
    // 32px SHGFI_LARGEICON icon. SHGFI_SYSICONINDEX + SHGetImageList yields that size with the
    // alpha channel intact, and it resolves .lnk targets, .url icons and folder desktop.ini
    // IconResource entries exactly the way Explorer does.
    internal const int SHIL_LARGE = 0;
    internal const int SHIL_EXTRALARGE = 2;
    internal const int SHIL_JUMBO = 4;
    internal const uint ILD_TRANSPARENT = 0x0001;
    internal const uint SHGFI_DISPLAYNAME = 0x00000200;
    internal const uint SHGFI_TYPENAME = 0x00000400;

    /// <summary>IID_IImageList: the interface the shell's system image lists are exposed through.</summary>
    internal static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [DllImport("shell32.dll", SetLastError = true)]
    internal static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppvObj);

    // ---- shell: namespace items -----------------------------------------
    /// <summary>SIGDN_NORMALDISPLAY: the plain, localized name Explorer writes under an icon.</summary>
    internal const uint SIGDN_NORMALDISPLAY = 0x00000000;

    /// <summary>IID_IShellItem.</summary>
    internal static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    /// <summary>
    /// Opens a shell namespace item from a parsing name such as the Recycle Bin's
    /// <c>::{645FF040-5081-101B-9F08-00AA002F954E}</c>. Namespace items are not files, so
    /// SHGetFileInfo cannot name them; this is the API the shell itself goes through.
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppv);

    /// <summary>IShellItem, declared only as far as its vtable position of GetDisplayName.</summary>
    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem? parent);

        [PreserveSig]
        int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string? name);

        [PreserveSig]
        int GetAttributes(uint sfgaoMask, out uint attributes);

        [PreserveSig]
        int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem? other, uint hint, out int order);
    }

    /// <summary>
    /// Works on the pointer SHGetImageList returns: the shell's IImageList is an image list
    /// handle wrapped in a COM object, so the comctl32 image-list API accepts it directly.
    /// </summary>
    [DllImport("comctl32.dll")]
    internal static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, uint flags);

    /// <summary>Icon at an explicit index inside a file, which .url shortcuts need.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    // ---- shell: resource strings ---------------------------------------
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, uint cchOutBuf, IntPtr ppvReserved);

    // ---- shell: execute -------------------------------------------------
    internal const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    internal const uint SEE_MASK_NOASYNC = 0x00000100;
    internal const uint SEE_MASK_FLAG_NO_UI = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    // ---- keyboard layouts ----------------------------------------------
    internal const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
    internal const uint KLF_ACTIVATE = 0x00000001;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    internal static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[]? lpList);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- misc -----------------------------------------------------------
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpaceEx(
        string directoryName, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    // ---- shell: namespace items addressed by PIDL ------------------------
    // SHGetFileInfo answers nothing for a parsing name like the Recycle Bin's "::{CLSID}", but it
    // does answer for the item's PIDL. That is how the virtual items get the icon Explorer draws,
    // including state-dependent ones such as the full or empty Recycle Bin.
    internal const uint SHGFI_PIDL = 0x00000008;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHParseDisplayName(
        string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    internal static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SHGetFileInfoPidl(
        IntPtr pidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    // A shell item's menu comes from the folder that owns it, never from the item: the folder is
    // handed the item's PIDL and asked for IShellFolder::GetUIObjectOf, or - for the desktop
    // itself - for its own view object. ILFindLastID splits an item's PIDL off its parent's.
    [DllImport("shell32.dll")]
    internal static extern int SHGetDesktopFolder(out IntPtr ppshf);

    [DllImport("shell32.dll", EntryPoint = "ILFindLastID")]
    internal static extern IntPtr ILFindLastID(IntPtr pidl);

    // ---- windows: enumeration, icons, activation -------------------------
    internal const uint GW_OWNER = 4;
    internal const int GWL_STYLE = -16;
    internal const int WS_EX_APPWINDOW = 0x00040000;
    internal const int WS_CAPTION = 0x00C00000;
    internal const int WS_MAXIMIZE = 0x01000000;
    internal const int WM_GETICON = 0x007F;
    internal const int ICON_SMALL2 = 2;
    internal const int ICON_BIG = 1;
    internal const int GCLP_HICON = -14;
    internal const int GCLP_HICONSM = -34;
    internal const int DWMWA_CLOAKED = 14;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int SW_MINIMIZE = 6;
    internal const int SW_RESTORE = 9;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint SMTO_ABORTIFHUNG = 0x0002;

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    /// <summary>Class icons are what most applications set, so the icon is taken from there.</summary>
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetClassLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    /// <summary>
    /// DWMWA_CLOAKED: 1 for a window that exists but is not on screen. Suspended UWP apps and
    /// apps parked in another virtual desktop stay cloaked, which is how "running windows only"
    /// is decided without guessing from the process list.
    /// </summary>
    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    /// <summary>
    /// DWMWA_EXTENDED_FRAME_BOUNDS: the part of a window the user actually sees. A maximized window's
    /// own rectangle runs a few pixels past every edge of its monitor - the invisible resize border
    /// DWM draws outside the frame - while these bounds stop at the work area, and that difference is
    /// what tells a maximized window apart from a fullscreen one.
    /// </summary>
    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>
    /// The same call, asked for a rectangle. It is declared under a name of its own rather than
    /// overloaded because <c>out var</c> cannot tell the two apart at the call site.
    /// </summary>
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static extern int DwmGetWindowBounds(IntPtr hWnd, int attribute, out RECT value, int size);

    // ---- still pictures of other applications' windows ------------------
    /// <summary>
    /// PW_RENDERFULLCONTENT: paint the picture DWM actually composes, not just what the window
    /// draws when asked with WM_PRINT. Without it a window whose client area is a hardware
    /// surface (browsers, Electron apps) comes back blank.
    /// </summary>
    internal const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int width, int height);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr hDc);

    // ---- the work area --------------------------------------------------
    internal const uint SPI_GETWORKAREA = 0x0030;
    internal const uint SPI_SETWORKAREA = 0x002F;
    internal const uint SPIF_SENDCHANGE = 0x0002;

    /// <summary>
    /// The work area is the part of the primary monitor's screen a maximized window is allowed to
    /// fill. The shell is what keeps a strip of it back for its taskbar, which is why the bar takes
    /// one back itself once it is the shell - see <see cref="WorkArea"/>.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfo(uint action, uint param, ref RECT value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindowAsync(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    // ---- menus: the shell context menu and its own items -----------------
    internal const uint MF_STRING = 0x00000000;
    internal const uint MF_SEPARATOR = 0x00000800;
    internal const uint MF_CHECKED = 0x00000008;
    internal const uint MF_GRAYED = 0x00000001;
    internal const uint MF_DISABLED = 0x00000002;
    internal const uint MF_POPUP = 0x00000010;
    internal const uint TPM_LEFTALIGN = 0x0000;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint WM_INITMENUPOPUP = 0x0117;
    internal const uint WM_DRAWITEM = 0x002B;
    internal const uint WM_MEASUREITEM = 0x002C;
    internal const uint WM_MENUCHAR = 0x0120;
    internal const int WH_CALLWNDPROC = 4;

    internal delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr idOrSubmenu, string? item);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    internal static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// The managed signature of CPOINT/POINT as marshalled into CMINVOKECOMMANDINFOEX.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public POINT ptInvoke;
    }

    internal const uint CMIC_MASK_UNICODE = 0x00004000;
    internal const uint CMIC_MASK_PTINVOKE = 0x20000000;
}
