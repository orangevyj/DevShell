using System.Runtime.InteropServices;
using System.Windows.Input;

namespace DevShell.Interop;

/// <summary>
/// One entry DevShell adds to the shell's own menu.
/// </summary>
/// <param name="Text">What the menu shows.</param>
/// <param name="Invoke">Runs when the entry is picked.</param>
internal sealed record ExtraMenuItem(string Text, Action Invoke)
{
    /// <summary>Entries sharing a group name are gathered into one submenu of that name.</summary>
    public string? Group { get; init; }

    public bool IsChecked { get; init; }

    public bool IsEnabled { get; init; } = true;
}

/// <summary>
/// Shows the shell's own context menu for one of the desktop's items - or for the desktop itself -
/// and invokes the verb that was picked. Going through the shell is what gives DevShell everything
/// Explorer's menus have: New, Paste, Paste shortcut, Display settings and Personalize on the
/// desktop; Open, Open with, Send to, Cut, Copy, Create shortcut, Delete, Rename and Properties on
/// an item; and whatever shell extensions are installed. DevShell's own settings are added as one
/// extra submenu rather than replacing any of it.
/// </summary>
internal sealed class ShellContextMenu : IDisposable
{
    private static readonly Guid IidContextMenu = new("000214e4-0000-0000-c000-000000000046");

    private const uint IdFirst = 1;
    private const uint IdLast = 0x6FFF;
    private const uint CmfNormal = 0x00000000;
    private const uint CmfExtendedVerbs = 0x00000100;
    private const int SwShowNormal = 1;
    private const uint WmNull = 0x0000;

    private object? _shellMenu;
    private IContextMenu2? _menu2;
    private IContextMenu3? _menu3;
    private NativeMethods.HookProc? _hookProc;
    private IntPtr _hook;

    /// <summary>
    /// Shows the shell's menu for <paramref name="parsingName"/> - a path, or a CLSID path such as
    /// the Recycle Bin's - at the current mouse position and runs the verb that was picked. Pass
    /// <c>null</c> for the desktop's own menu, the one a wallpaper right-click gets in Explorer.
    /// <paramref name="owner"/> is the window the menu belongs to; it has to be able to take the
    /// foreground or the menu will not close when the user clicks somewhere else.
    /// </summary>
    public void Show(IntPtr owner, string? parsingName, IReadOnlyList<ExtraMenuItem> extras)
    {
        var what = parsingName ?? "the desktop";
        var contextMenu = OpenShellMenu(owner, parsingName);
        if (contextMenu is null)
        {
            return;
        }

        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        var popups = new List<IntPtr>();
        try
        {
            var flags = CmfNormal | (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? CmfExtendedVerbs : 0);
            var queried = contextMenu.QueryContextMenu(menu, 0, IdFirst, IdLast, flags);
            if (queried < 0)
            {
                App.LogInfo($"ShellContextMenu: QueryContextMenu failed (0x{queried:X8}) for '{what}'");
                return;
            }

            // The HRESULT carries the highest command id the shell used, so DevShell's own entries
            // start above it and cannot collide with a shell verb.
            var nextId = IdFirst + (uint)(queried & 0xFFFF);
            var mine = AppendExtras(menu, extras, ref nextId, popups);

            Run(owner, menu, contextMenu, mine);
        }
        catch (Exception ex)
        {
            App.Log("ShellContextMenu.Show", ex);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
            foreach (var popup in popups)
            {
                NativeMethods.DestroyMenu(popup);
            }
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        _hookProc = null;
        _menu2 = null;
        _menu3 = null;

        if (_shellMenu is not null)
        {
            try
            {
                Marshal.ReleaseComObject(_shellMenu);
            }
            catch (Exception ex)
            {
                App.Log("ShellContextMenu.Release", ex);
            }

            _shellMenu = null;
        }
    }

    /// <summary>
    /// Asks the folder that owns the item for the item's menu handler - a shell item never hands
    /// one out itself. Passing a <c>null</c> parsing name asks the desktop folder for its own view
    /// object instead, which is the menu Explorer shows when the wallpaper is right-clicked.
    /// </summary>
    private IContextMenu? OpenShellMenu(IntPtr owner, string? parsingName)
    {
        IntPtr contextMenu = IntPtr.Zero;
        IShellFolder? desktop = null;
        try
        {
            var hr = NativeMethods.SHGetDesktopFolder(out var folderPointer);
            if (hr != 0 || folderPointer == IntPtr.Zero)
            {
                App.LogInfo($"ShellContextMenu: the desktop folder is unavailable (0x{hr:X8})");
                return null;
            }

            desktop = (IShellFolder)Marshal.GetObjectForIUnknown(folderPointer);
            Marshal.Release(folderPointer);

            var iid = IidContextMenu;
            if (parsingName is null)
            {
                hr = desktop.CreateViewObject(owner, ref iid, out contextMenu);
            }
            else
            {
                hr = NativeMethods.SHParseDisplayName(parsingName, IntPtr.Zero, out var pidl, 0, out _);
                if (hr != 0 || pidl == IntPtr.Zero)
                {
                    App.LogInfo($"ShellContextMenu: '{parsingName}' did not resolve (0x{hr:X8})");
                    return null;
                }

                try
                {
                    var array = Marshal.AllocHGlobal(IntPtr.Size);
                    try
                    {
                        Marshal.WriteIntPtr(array, NativeMethods.ILFindLastID(pidl));
                        hr = desktop.GetUIObjectOf(owner, 1, array, ref iid, IntPtr.Zero, out contextMenu);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(array);
                    }
                }
                finally
                {
                    NativeMethods.ILFree(pidl);
                }
            }

            if (hr != 0 || contextMenu == IntPtr.Zero)
            {
                App.LogInfo($"ShellContextMenu: no context menu for '{parsingName ?? "the desktop"}' (0x{hr:X8})");
                return null;
            }

            _shellMenu = Marshal.GetObjectForIUnknown(contextMenu);
            if (_shellMenu is not IContextMenu menu)
            {
                return null;
            }

            // The shell fills its submenus (New, Send to, Open with, ...) itself, through the
            // extended interfaces; without them those submenus would come up empty.
            _menu2 = _shellMenu as IContextMenu2;
            _menu3 = _shellMenu as IContextMenu3;

            return menu;
        }
        catch (Exception ex)
        {
            App.Log("ShellContextMenu.Open", ex);
            return null;
        }
        finally
        {
            if (contextMenu != IntPtr.Zero)
            {
                Marshal.Release(contextMenu);
            }

            if (desktop is not null)
            {
                Marshal.ReleaseComObject(desktop);
            }
        }
    }

    private void Run(IntPtr owner, IntPtr menu, IContextMenu contextMenu, Dictionary<int, ExtraMenuItem> mine)
    {
        NativeMethods.GetCursorPos(out var point);

        if (!WindowActivation.BringToFront(owner))
        {
            App.LogInfo("ShellContextMenu: the menu owner could not take the foreground");
        }

        _hookProc = MenuMessageHook;
        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_CALLWNDPROC, _hookProc, IntPtr.Zero, NativeMethods.GetCurrentThreadId());

        int command;
        try
        {
            command = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_LEFTALIGN,
                point.X, point.Y, owner, IntPtr.Zero);
        }
        finally
        {
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }

            _hookProc = null;
            _menu2 = null;
            _menu3 = null;

            // Documented follow-up: the owner needs a message after the menu closes, otherwise the
            // next click goes to the menu instead of the window under the pointer.
            NativeMethods.PostMessage(owner, WmNull, IntPtr.Zero, IntPtr.Zero);
        }

        if (command == 0)
        {
            // Dismissed without choosing anything.
            return;
        }

        if (mine.TryGetValue(command, out var extra))
        {
            extra.Invoke();
            return;
        }

        InvokeVerb(contextMenu, owner, command, point);
    }

    private static void InvokeVerb(IContextMenu contextMenu, IntPtr owner, int command, NativeMethods.POINT point)
    {
        var offset = new IntPtr(command - IdFirst);

        // Commands come back as MAKEINTRESOURCE relative to idCmdFirst, and the offset has to go in
        // BOTH verb members. A handler that follows the newer documentation reads lpVerbW; one that
        // follows the older reads lpVerb. Leaving lpVerb zero does not mean "no verb" - zero is
        // MAKEINTRESOURCE(0), a valid offset - so such a handler runs the first command in its own
        // menu instead of the one that was chosen, which is why every shell entry used to end up
        // launching the same program.
        var info = new NativeMethods.CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.CMINVOKECOMMANDINFOEX>(),
            fMask = NativeMethods.CMIC_MASK_UNICODE | NativeMethods.CMIC_MASK_PTINVOKE,
            hwnd = owner,
            nShow = SwShowNormal,
            lpVerb = offset,
            lpVerbW = offset,
            ptInvoke = point,
        };

        try
        {
            contextMenu.InvokeCommand(ref info);
        }
        catch (Exception ex)
        {
            App.Log("ShellContextMenu.InvokeCommand", ex);
        }
    }

    private static Dictionary<int, ExtraMenuItem> AppendExtras(
        IntPtr menu, IReadOnlyList<ExtraMenuItem> extras, ref uint nextId, List<IntPtr> popups)
    {
        var map = new Dictionary<int, ExtraMenuItem>();

        if (extras.Count == 0)
        {
            return map;
        }

        NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);

        // DevShell's own single entries come first and the submenu last: the singles are the ones
        // the menu gets scanned for, and the settings that would otherwise crowd them out sit
        // together underneath.
        foreach (var item in extras.Where(item => item.Group is null))
        {
            AppendOne(menu, item, ref nextId, map);
        }

        foreach (var group in extras.Where(item => item.Group is not null).GroupBy(item => item.Group!))
        {
            var popup = NativeMethods.CreatePopupMenu();
            if (popup == IntPtr.Zero)
            {
                continue;
            }

            popups.Add(popup);

            foreach (var item in group)
            {
                AppendOne(popup, item, ref nextId, map);
            }

            NativeMethods.AppendMenu(menu, NativeMethods.MF_POPUP, popup, group.Key);
        }

        return map;
    }

    private static void AppendOne(
        IntPtr menu, ExtraMenuItem item, ref uint nextId, Dictionary<int, ExtraMenuItem> map)
    {
        var id = nextId++;
        var flags = NativeMethods.MF_STRING
                    | (item.IsChecked ? NativeMethods.MF_CHECKED : 0u)
                    | (item.IsEnabled ? 0u : NativeMethods.MF_GRAYED | NativeMethods.MF_DISABLED);

        if (NativeMethods.AppendMenu(menu, flags, new IntPtr(id), item.Text))
        {
            map[(int)id] = item;
        }
    }

    /// <summary>
    /// The shell's submenus are filled in when Windows asks a menu to initialize, which happens
    /// inside TrackPopupMenu's own message loop. This hook is what keeps those messages reaching
    /// the shell's IContextMenu2/IContextMenu3 implementation - without it the New submenu and
    /// Send to would come up empty, because TrackPopupMenu listens on a window we do not control.
    /// </summary>
    private IntPtr MenuMessageHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            // lParam is a CWPSTRUCT: { LPARAM lParam; WPARAM wParam; UINT message; }
            var message = (uint)Marshal.ReadInt32(lParam, IntPtr.Size * 2);
            var messageWParam = Marshal.ReadIntPtr(lParam, IntPtr.Size);
            var messageLParam = Marshal.ReadIntPtr(lParam, 0);

            switch (message)
            {
                case NativeMethods.WM_INITMENUPOPUP:
                case NativeMethods.WM_DRAWITEM:
                case NativeMethods.WM_MEASUREITEM:
                    _menu2?.HandleMenuMsg(message, messageWParam, messageLParam);
                    break;

                case NativeMethods.WM_MENUCHAR:
                    if (_menu3 is not null)
                    {
                        // S_OK means the shell handled the keystroke; its answer is the LRESULT.
                        var handled = _menu3.HandleMenuMsg2(message, messageWParam, messageLParam, out var result);
                        if (handled == 0)
                        {
                            return result;
                        }
                    }

                    break;
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    // The four shell menu interfaces are told apart by GUID alone - their vtable layouts are
    // identical up to the last method - so a wrong GUID silently calls the wrong slot.
    [ComImport]
    [Guid("000214e4-0000-0000-c000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr menu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);

        void InvokeCommand(ref NativeMethods.CMINVOKECOMMANDINFOEX info);

        void GetCommandString(IntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint max);
    }

    [ComImport]
    [Guid("000214f4-0000-0000-c000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr menu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);

        void InvokeCommand(ref NativeMethods.CMINVOKECOMMANDINFOEX info);

        void GetCommandString(IntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint max);

        [PreserveSig]
        int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
    }

    [ComImport]
    [Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr menu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);

        void InvokeCommand(ref NativeMethods.CMINVOKECOMMANDINFOEX info);

        void GetCommandString(IntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint max);

        [PreserveSig]
        int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);

        [PreserveSig]
        int HandleMenuMsg2(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    /// <summary>
    /// A folder hands out the menu handlers of the items it owns. Only CreateViewObject and
    /// GetUIObjectOf are called; the methods before them are declared because the slots have to
    /// line up with the real interface (IShellFolder's order is ParseDisplayName, EnumObjects,
    /// BindToObject, BindToStorage, CompareIDs, CreateViewObject, GetAttributesOf, GetUIObjectOf).
    /// </summary>
    [ComImport]
    [Guid("000214e6-0000-0000-c000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(IntPtr hwnd, IntPtr bindCtx, IntPtr name, IntPtr eaten, out IntPtr pidl, IntPtr attributes);

        [PreserveSig]
        int EnumObjects(IntPtr hwnd, uint flags, out IntPtr enumIdList);

        [PreserveSig]
        int BindToObject(IntPtr pidl, IntPtr bindCtx, ref Guid riid, out IntPtr result);

        [PreserveSig]
        int BindToStorage(IntPtr pidl, IntPtr bindCtx, ref Guid riid, out IntPtr result);

        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr first, IntPtr second);

        [PreserveSig]
        int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr result);

        [PreserveSig]
        int GetAttributesOf(uint count, IntPtr pidls, ref uint attributes);

        [PreserveSig]
        int GetUIObjectOf(IntPtr hwndOwner, uint count, IntPtr pidls, ref Guid riid, IntPtr reserved, out IntPtr result);
    }
}
