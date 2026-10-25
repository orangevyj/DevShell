using System.Globalization;

namespace DevShell.Interop;

internal sealed record KeyboardLayout(IntPtr Handle, string Code, string Name)
{
    public string Klid => ((uint)Handle.ToInt64()).ToString("X8");
}

/// <summary>
/// The active keyboard layout of whatever window has focus, plus switching between
/// installed layouts by posting WM_INPUTLANGCHANGEREQUEST to that window.
/// </summary>
internal static class KeyboardLayouts
{
    public static KeyboardLayout Current()
    {
        var hkl = NativeMethods.GetForegroundWindow() is var foreground && foreground != IntPtr.Zero
            ? NativeMethods.GetKeyboardLayout(NativeMethods.GetWindowThreadProcessId(foreground, out _))
            : NativeMethods.GetKeyboardLayout(0);

        if (hkl == IntPtr.Zero)
        {
            hkl = NativeMethods.GetKeyboardLayout(0);
        }

        return Describe(hkl);
    }

    public static IReadOnlyList<KeyboardLayout> Installed()
    {
        var buffer = new IntPtr[64];
        var count = NativeMethods.GetKeyboardLayoutList(buffer.Length, buffer);
        var list = new List<KeyboardLayout>(Math.Max(count, 1));

        for (var i = 0; i < count && i < buffer.Length; i++)
        {
            list.Add(Describe(buffer[i]));
        }

        if (list.Count == 0)
        {
            list.Add(Current());
        }

        return list;
    }

    public static bool Activate(KeyboardLayout layout)
    {
        try
        {
            NativeMethods.LoadKeyboardLayout(layout.Klid, NativeMethods.KLF_ACTIVATE);

            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                return false;
            }

            // This is the documented way to change the layout of another window's thread.
            return NativeMethods.PostMessage(
                foreground, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, layout.Handle);
        }
        catch (Exception ex)
        {
            App.Log("KeyboardLayouts.Activate", ex);
            return false;
        }
    }

    public static bool CycleNext()
    {
        var installed = Installed();
        if (installed.Count < 2)
        {
            return false;
        }

        var current = Current();
        var index = 0;
        for (var i = 0; i < installed.Count; i++)
        {
            if (installed[i].Handle == current.Handle)
            {
                index = i;
                break;
            }
        }

        return Activate(installed[(index + 1) % installed.Count]);
    }

    private static KeyboardLayout Describe(IntPtr hkl)
    {
        var langId = (int)(hkl.ToInt64() & 0xFFFF);

        try
        {
            var culture = CultureInfo.GetCultureInfo(langId);
            var code = culture.ThreeLetterISOLanguageName.ToUpperInvariant();
            if (code.Length > 3)
            {
                code = code[..3];
            }

            var name = string.IsNullOrWhiteSpace(culture.EnglishName) ? culture.Name : culture.EnglishName;
            return new KeyboardLayout(hkl, code, $"{name} ({culture.Name})");
        }
        catch
        {
            return new KeyboardLayout(hkl, $"0x{langId:X4}", $"Layout 0x{langId:X8}");
        }
    }
}
