using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DevShell.Interop;

/// <summary>
/// Watches the keyboard just long enough to notice the Windows key pressed on its own. It has to be a
/// low-level hook: a bare Windows key is not a hotkey combination, so RegisterHotKey cannot ask for
/// it, and once Explorer is no longer the shell nothing else in the system reports it either.
/// </summary>
/// <remarks>
/// <para>
/// What happens to the Windows key is decided afresh on every press, by asking whether anything
/// besides DevShell would answer it. Explorer's taskbar on screen (<c>Shell_TrayWnd</c>) is the plain
/// case, but it is not the only one: Windows starts Explorer's own Start hosts for a session whose
/// shell is DevShell just the same, and one of them raising its Start menu over DevShell's is what
/// "the Windows key opened the wrong Start menu" looked like.
/// </para>
/// <para>
/// While Explorer's taskbar is there, the real Windows key is withheld: its down, its auto-repeat and
/// its up never reach the system, so win32k never sees a Windows key that was never released — the
/// state that used to leave <c>VK_LWIN</c> logically down for the whole machine and turn every later
/// keystroke into a Win+key combination (Win+Space eating the space bar on a Russian layout). A bare
/// press and release still raises <see cref="WindowsKeyPressed"/> and injects nothing, and that is
/// also what stops Explorer from putting its own Start menu up underneath DevShell's.
/// </para>
/// <para>
/// The first key pressed while the Windows key is held is handed back as one balanced chord in a
/// single <c>SendInput</c> batch — the withheld modifiers down in press order, that key down and up,
/// then those modifiers up in reverse: the chord has to carry the withheld Windows key or Win+Shift+S
/// would arrive as Shift+S, and it has to be balanced or the key it is combined with is left stuck
/// down. Only modifiers are re-armed in the later chords of the same press; a plain key is replayed
/// once and never again, so holding Win and typing D then E cannot send Win+D twice and toggle the
/// desktop back.
/// </para>
/// <para>
/// When nothing else would answer — no Explorer taskbar and no Windows Start host to compete with —
/// nothing is withheld and nothing is injected at all: the press is pure observation, and the whole
/// class of stuck-key bugs is impossible there. Combinations reach the applications that want them
/// untouched. On a machine that does have a Windows Start menu, the withholding path above is the one
/// taken, and the bar still gets first refusal on a bare chord while it is the shell.
/// </para>
/// </remarks>
internal sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;

    // KEYBDINPUT.dwFlags
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;

    // KBDLLHOOKSTRUCT.flags
    private const uint LlkhfExtended = 0x01;
    private const uint LlkhfInjected = 0x10;

    private readonly LowLevelKeyboardProc _callback;

    // In press order, so the chord can re-arm them in the order the user held them.
    private readonly List<HeldKey> _heldModifiers = new();

    // Every key whose real down was swallowed, and whose real up therefore has to be swallowed too.
    private readonly HashSet<int> _swallowedKeys = new();

    /// <summary>
    /// Processes whose presence means a Windows Start menu can still be raised for the bare Windows
    /// key. "explorer" covers the shell itself; the two hosts are what Explorer hands the key to on
    /// the Windows builds that moved the Start menu out of explorer.exe.
    /// </summary>
    private static readonly string[] OtherShellProcesses =
        ["explorer", "StartMenuExperienceHost", "ShellExperienceHost"];

    private IntPtr _hook;
    private bool _windowsPressed;
    private bool _usedWithAnotherKey;
    private bool _withholding;

    public KeyboardHook()
    {
        // Held in a field for the hook's whole life: Windows keeps the raw function pointer, and a
        // delegate that has been collected takes the process down with it the next time a key is hit.
        _callback = Handle;
    }

    /// <summary>Raised on the UI thread when the Windows key is pressed and released by itself.</summary>
    public event Action? WindowsKeyPressed;

    /// <summary>
    /// Raised on the UI thread for a bare Win+key chord while DevShell is the shell, with the virtual
    /// key that was pressed. Returning true keeps the chord away from the system: nothing else would
    /// act on it there, and it must not end up typed into whichever application is in front.
    /// </summary>
    public event Func<int, bool>? WindowsKeyChord;

    public bool IsInstalled => _hook != IntPtr.Zero;

    public bool Install()
    {
        if (IsInstalled)
        {
            return true;
        }

        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(null), 0);

        if (_hook == IntPtr.Zero)
        {
            App.Log("KeyboardHook.Install", new Exception($"SetWindowsHookEx failed with win32 error {Marshal.GetLastWin32Error()}"));
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>The Windows key, either one: the modifier that turns a key into a shell chord.</summary>
    private static bool IsWindowsKey(int vk) => vk is VkLeftWindows or VkRightWindows;

    private IntPtr Handle(int code, IntPtr message, IntPtr data)
    {
        // A negative code means "hand this to the next hook without looking at it" by contract.
        if (code < 0)
        {
            return CallNextHookEx(_hook, code, message, data);
        }

        var vk = Marshal.ReadInt32(data);
        var scanCode = (uint)Marshal.ReadInt32(data, 4);
        var flags = (uint)Marshal.ReadInt32(data, 8);
        var extended = (flags & LlkhfExtended) != 0;

        // Our own chords come back through here; looking at them would make us replay ourselves.
        if ((flags & LlkhfInjected) != 0)
        {
            return CallNextHookEx(_hook, code, message, data);
        }

        var isWindowsKey = vk is VkLeftWindows or VkRightWindows;

        switch ((int)message)
        {
            case WmKeyDown:
            case WmSysKeyDown:
                return HandleKeyDown(vk, scanCode, extended, isWindowsKey)
                    ? new IntPtr(1)
                    : CallNextHookEx(_hook, code, message, data);

            case WmKeyUp:
            case WmSysKeyUp:
                return HandleKeyUp(vk, isWindowsKey)
                    ? new IntPtr(1)
                    : CallNextHookEx(_hook, code, message, data);

            default:
                return CallNextHookEx(_hook, code, message, data);
        }
    }

    /// <summary>Returns true when the event must not reach the system.</summary>
    private bool HandleKeyDown(int vk, uint scanCode, bool extended, bool isWindowsKey)
    {
        if (isWindowsKey)
        {
            if (_windowsPressed)
            {
                // Auto-repeat of a press that is already being tracked: keep the decision of that press.
                return _withholding;
            }

            // A fresh press decides everything: stale state from a release lost to a secure desktop
            // must not shadow it, and the answer to "would anyone else answer this key?" is asked now.
            _heldModifiers.Clear();
            _swallowedKeys.Clear();
            _usedWithAnotherKey = false;
            _withholding = AnotherShellWouldAnswerTheKey();
            _windowsPressed = true;
            _heldModifiers.Add(new HeldKey(vk, scanCode, extended));

            if (_withholding)
            {
                _swallowedKeys.Add(vk);
            }

            return _withholding;
        }

        if (!_windowsPressed)
        {
            return false;
        }

        _usedWithAnotherKey = true;

        if (vk is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5)
        {
            if (_heldModifiers.Any(held => held.Vk == vk))
            {
                // Auto-repeat of a modifier we are already re-arming.
                return _withholding;
            }

            // A modifier joins the chord rather than ending it: it is re-armed in every later chord.
            _heldModifiers.Add(new HeldKey(vk, scanCode, extended));

            if (_withholding)
            {
                _swallowedKeys.Add(vk);
            }

            return _withholding;
        }

        if (!_withholding)
        {
            // Nothing else would answer the key: no Explorer taskbar and no Windows Start host, so the
            // bar is asked first. Only a bare Win+key is offered - anything with Shift, Ctrl or Alt on
            // it belongs to Windows or to whatever has it registered (Win+Shift+S and the rest) - and a
            // chord the bar does not take is passed on exactly as before.
            var bare = !_heldModifiers.Any(held => !IsWindowsKey(held.Vk));
            if (!bare || WindowsKeyChord?.Invoke(vk) != true)
            {
                return false;
            }

            // The chord is DevShell's, so that key - and only that key - is kept from the system.
            _swallowedKeys.Add(vk);
            return true;
        }

        // Something besides DevShell answers Windows keys as well - Explorer's taskbar, or a Start
        // host it started, or Explorer itself. The bar still gets first refusal on a bare chord while
        // it is the shell, which is the same decision the branch above makes from the other side; a
        // chord it does not take is replayed to Windows below.
        var bareChord = !_heldModifiers.Any(held => !IsWindowsKey(held.Vk));

        if (bareChord && WindowsKeyChord?.Invoke(vk) == true)
        {
            _swallowedKeys.Add(vk);
            return true;
        }

        // A plain key is played once, as part of one balanced chord, and never re-armed.
        _swallowedKeys.Add(vk);
        SendChord(vk, scanCode, extended);

        return true;
    }

    /// <summary>Returns true when the event must not reach the system.</summary>
    private bool HandleKeyUp(int vk, bool isWindowsKey)
    {
        if (!_swallowedKeys.Remove(vk))
        {
            if (isWindowsKey && _windowsPressed)
            {
                var alone = !_usedWithAnotherKey;
                EndPress();

                if (alone)
                {
                    WindowsKeyPressed?.Invoke();
                }
            }
            else if (_windowsPressed)
            {
                _heldModifiers.RemoveAll(held => held.Vk == vk);
            }

            return false;
        }

        _heldModifiers.RemoveAll(held => held.Vk == vk);

        // Only once no Windows key is left held does the press end — and with it the suppressing.
        if (isWindowsKey && !_heldModifiers.Any(held => held.Vk is VkLeftWindows or VkRightWindows))
        {
            var alone = !_usedWithAnotherKey;
            EndPress();

            if (alone)
            {
                WindowsKeyPressed?.Invoke();
            }
        }

        return true;
    }

    private void EndPress()
    {
        _windowsPressed = false;
        _usedWithAnotherKey = false;
        _withholding = false;
        _heldModifiers.Clear();
        _swallowedKeys.Clear();
    }

    /// <summary>
    /// Whether something besides DevShell would answer a Windows key press. Explorer's taskbar is the
    /// plain case; Explorer's Start hosts are the one that used to be missed, because Windows starts
    /// them for a session whose shell is DevShell just the same - and one of them putting its own Start
    /// menu up is exactly what "the Windows key opened the wrong Start menu" looked like.
    /// </summary>
    private static bool AnotherShellWouldAnswerTheKey()
    {
        if (FindWindow("Shell_TrayWnd", null) is var tray && tray != IntPtr.Zero)
        {
            // DevShell's own notification-area host carries this class name, so a window of ours is
            // not somebody else answering the key.
            NativeMethods.GetWindowThreadProcessId(tray, out var owner);
            if (owner != (uint)Environment.ProcessId)
            {
                return true;
            }
        }

        foreach (var name in OtherShellProcesses)
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                // A process list that refuses to be read is not a reason to throw from a keyboard hook;
                // the next name still gets its chance.
                App.Log("KeyboardHook.AnotherShellWouldAnswerTheKey", ex);
            }
        }

        return false;
    }

    /// <summary>
    /// Hands one combination back to the system: the withheld modifiers down, this key down and up,
    /// then those modifiers up in reverse order. All of it in one batch, because the ordering across
    /// separate calls is what leaves keys stuck.
    /// </summary>
    private void SendChord(int vk, uint scanCode, bool extended)
    {
        var inputs = new List<INPUT>(_heldModifiers.Count * 2 + 2);

        foreach (var held in _heldModifiers)
        {
            inputs.Add(KeyInput(held.Vk, held.ScanCode, held.Extended, up: false));
        }

        inputs.Add(KeyInput(vk, scanCode, extended, up: false));
        inputs.Add(KeyInput(vk, scanCode, extended, up: true));

        for (var index = _heldModifiers.Count - 1; index >= 0; index--)
        {
            var held = _heldModifiers[index];
            inputs.Add(KeyInput(held.Vk, held.ScanCode, held.Extended, up: true));
        }

        var array = inputs.ToArray();
        var sent = SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());

        if (sent != array.Length)
        {
            App.Log("KeyboardHook.SendChord", new Exception($"SendInput sent {sent} of {array.Length} events, win32 error {Marshal.GetLastWin32Error()}"));
        }
    }

    private static INPUT KeyInput(int vk, uint scanCode, bool extended, bool up)
    {
        var flags = 0u;

        if (extended)
        {
            flags |= KeyEventExtendedKey;
        }

        if (up)
        {
            flags |= KeyEventKeyUp;
        }

        return new INPUT
        {
            type = 1,
            ki = new KEYBDINPUT
            {
                wVk = (ushort)vk,
                wScan = (ushort)scanCode,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        };
    }

    private readonly record struct HeldKey(int Vk, uint ScanCode, bool Extended);

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // x64 INPUT is 40 bytes — 4 for the type, 4 of padding, then the 32-byte input union. The stride
    // matters: SendInput walks the array with it and silently drops everything when it is wrong.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)]
        public uint type;

        [FieldOffset(8)]
        public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hook, LowLevelKeyboardProc callback, IntPtr module, uint thread);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? name);
}
