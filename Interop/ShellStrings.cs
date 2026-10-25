using System.Runtime.InteropServices;
using System.Text;

namespace DevShell.Interop;

/// <summary>
/// Shell resource strings. desktop.ini and shortcut metadata store names and icons as
/// indirect references like <c>@%SystemRoot%\system32\shell32.dll,-22067</c>; resolving those
/// through the shell is what makes localized names come out the way Explorer shows them
/// instead of as raw resource paths.
/// </summary>
internal static class ShellStrings
{
    public static string? Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        // Only "@..." values are indirect; anything else is already the literal text.
        if (!trimmed.StartsWith('@'))
        {
            return trimmed;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var hr = NativeMethods.SHLoadIndirectString(trimmed, buffer, (uint)buffer.Capacity, IntPtr.Zero);
            if (hr == 0 && buffer.Length > 0)
            {
                return buffer.ToString();
            }

            App.LogInfo($"ShellStrings: SHLoadIndirectString failed (0x{hr:X8}) for '{trimmed}'");
        }
        catch (Exception ex)
        {
            App.Log("ShellStrings.Resolve", ex);
        }

        return null;
    }

    /// <summary>
    /// The name the shell shows for a file. For a shortcut that is the name of what it points at, which
    /// is how Windows' own menus end up saying "Device Manager" and "Programs and features" where the
    /// file names only offer "05 - Device Manager.lnk" and "10 - AppsAndFeatures.lnk". Null when the
    /// shell has nothing to say about the item.
    /// </summary>
    public static string? DisplayName(string path)
    {
        try
        {
            var info = new NativeMethods.SHFILEINFO();
            NativeMethods.SHGetFileInfo(
                path, 0, ref info, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), NativeMethods.SHGFI_DISPLAYNAME);

            return string.IsNullOrWhiteSpace(info.szDisplayName) ? null : info.szDisplayName;
        }
        catch (Exception ex)
        {
            App.Log("ShellStrings.DisplayName", ex);
            return null;
        }
    }
}
