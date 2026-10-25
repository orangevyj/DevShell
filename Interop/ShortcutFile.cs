using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DevShell.Interop;

/// <summary>
/// Reading a shortcut by hand. The shell resolves .lnk files for us when we ShellExecute them, but
/// there are two things about a shortcut DevShell needs to know beforehand: whether it was saved to
/// run elevated, and whether it points at Explorer with nothing else to say - which is the shell's
/// "start the desktop" command rather than "open a window".
/// </summary>
internal static class ShortcutFile
{
    /// <summary>
    /// Where the flags sit in a .lnk: the 4-byte header size, then the 16-byte class id, then the
    /// flags. <c>RunAsUser</c> is the bit the shell's "Run as administrator" tick sets.
    /// </summary>
    private const int FlagsOffset = 20;
    private const uint RunAsUser = 0x00002000;

    private static readonly Type? ScriptShell = Type.GetTypeFromProgID("WScript.Shell");

    public static bool IsShortcut(string path) => path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the shortcut is marked to run elevated. That is a flag inside the file rather than
    /// something the shell's shortcut interfaces hand back, so it is read straight out of the header.
    /// </summary>
    public static bool IsElevated(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[FlagsOffset + 4];

            if (stream.Read(header, 0, header.Length) < header.Length)
            {
                return false;
            }

            return (BitConverter.ToUInt32(header, FlagsOffset) & RunAsUser) != 0;
        }
        catch (Exception ex)
        {
            App.Log("ShortcutFile.IsElevated", ex);
            return false;
        }
    }

    /// <summary>
    /// What the shortcut points at, or null when it cannot be read. WScript.Shell is asked instead of
    /// the IShellLink interface because it resolves the same shortcuts with no vtable to get wrong,
    /// and it answers for the shell's own namespace shortcuts (the ones carrying a <c>shell:::</c>
    /// target) as well.
    /// </summary>
    public static (string Target, string Arguments)? Target(string path)
    {
        if (!IsShortcut(path) || ScriptShell is null)
        {
            return null;
        }

        object? shell = null;
        object? link = null;

        try
        {
            shell = Activator.CreateInstance(ScriptShell);
            if (shell is null)
            {
                return null;
            }

            link = ScriptShell.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            if (link is null)
            {
                return null;
            }

            var type = link.GetType();
            var target = type.InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
            var arguments = type.InvokeMember("Arguments", BindingFlags.GetProperty, null, link, null) as string;

            return (target ?? string.Empty, arguments ?? string.Empty);
        }
        catch (Exception ex)
        {
            App.Log("ShortcutFile.Target", ex);
            return null;
        }
        finally
        {
            Release(link);
            Release(shell);
        }
    }

    /// <summary>
    /// Whether running this shortcut would start Explorer's own desktop. A shortcut whose target is
    /// explorer.exe with nothing else to say is the shell's "start the desktop" command: on a machine
    /// where DevShell is the shell that puts a second desktop on the screen, so DevShell opens a plain
    /// file window instead.
    /// </summary>
    public static bool PointsAtBareExplorer(string path)
    {
        if (Target(path) is not { } link)
        {
            return false;
        }

        return link.Target.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)
            && link.Arguments.Trim().Length == 0;
    }

    private static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }
}
