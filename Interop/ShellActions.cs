using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using DevShell.Services;

namespace DevShell.Interop;

internal enum LaunchResult
{
    Success,
    Cancelled,
    Failed,
}

/// <summary>
/// Everything that hands work over to the shell: open a file, reveal it in Explorer,
/// or start it elevated. Elevation deliberately goes through ShellExecuteEx's "runas"
/// verb so Windows shows its own UAC prompt instead of the bar asking for anything.
/// </summary>
internal static class ShellActions
{
    public static LaunchResult Open(string path)
    {
        if (Directory.Exists(path))
        {
            return OpenExplorerWindow(path);
        }

        // A shortcut that only says "explorer.exe" is the shell's "start the desktop" command. With
        // DevShell as the shell that would put a second desktop on the screen behind the bar, so it
        // becomes a "This PC" window, which is what the same shortcut opens in Windows.
        if (ShortcutFile.PointsAtBareExplorer(path))
        {
            return OpenExplorerWindow(null);
        }

        return Invoke(null, path, null);
    }

    /// <summary>
    /// Opens a file window - never a second desktop. Explorer with no arguments at all is the command
    /// that starts the desktop, so the "This PC" namespace gets named explicitly instead.
    /// </summary>
    public static LaunchResult OpenExplorerWindow(string? path)
    {
        return string.IsNullOrEmpty(path)
            ? StartProcess("explorer.exe", "shell:MyComputerFolder")
            : StartProcess("explorer.exe", Quote(path));
    }

    /// <summary>
    /// Windows' own Run dialog. It is shell32's RunFileDlg, which Explorer puts up for Win+R on every
    /// Windows there is; it is reached through rundll32 by ordinal because shell32 exports it under no
    /// name. Using the real dialog means the line typed into it is resolved by the same code, with the
    /// same history and the same completion, as on a machine that runs Explorer.
    /// </summary>
    public static LaunchResult ShowRunDialog()
    {
        var result = StartProcess("rundll32.exe", "shell32.dll,#61");

        if (result == LaunchResult.Failed)
        {
            App.Log("ShellActions.ShowRunDialog", new Exception("shell32's RunFileDlg would not start"));
        }

        return result;
    }

    public static LaunchResult OpenFileLocation(string path)
    {
        if (Directory.Exists(path))
        {
            return StartProcess("explorer.exe", Quote(path));
        }

        // /select, must be passed as one argument with the quoted path attached.
        if (!File.Exists(path))
        {
            return LaunchResult.Failed;
        }

        return StartProcess("explorer.exe", $"/select,{Quote(path)}");
    }

    public static LaunchResult OpenAsAdministrator(string path, out string? error)
    {
        error = null;

        if (Directory.Exists(path))
        {
            error = Strings.T("Msg_DirElevation");
            return LaunchResult.Failed;
        }

        if (!File.Exists(path))
        {
            error = Strings.T("Msg_FileGone");
            return LaunchResult.Failed;
        }

        var result = Invoke("runas", path, null);
        if (result == LaunchResult.Failed)
        {
            error = Strings.T("Msg_ElevationFailed");
        }

        return result;
    }

    public static LaunchResult Run(string path, string? arguments)
    {
        return Invoke(null, path, arguments);
    }

    private static LaunchResult Invoke(string? verb, string file, string? parameters)
    {
        var info = new NativeMethods.SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.SHELLEXECUTEINFO>(),
            fMask = NativeMethods.SEE_MASK_NOASYNC | NativeMethods.SEE_MASK_FLAG_NO_UI,
            hwnd = IntPtr.Zero,
            lpVerb = verb,
            lpFile = file,
            lpParameters = parameters,
            nShow = 1,
        };

        if (!NativeMethods.ShellExecuteEx(ref info))
        {
            var code = Marshal.GetLastWin32Error();
            // 1223 == ERROR_CANCELLED: the user said no to the UAC prompt, not an error.
            if (code == 1223)
            {
                return LaunchResult.Cancelled;
            }

            // A target that names a scheme - ms-settings:, and every other URI a program registers
            // itself under - is resolved by the shell's own association table, and that table is one
            // of the things Explorer *is* rather than something it runs, so ShellExecuteEx cannot
            // resolve it here. Explorer itself still can: handed a URI it carries out the association
            // and goes away again without a desktop, so it is asked. This is how Settings opens on a
            // session whose shell is DevShell.
            if (verb is null && string.IsNullOrEmpty(parameters) && IsScheme(file))
            {
                var delegated = StartProcess("explorer.exe", file);

                if (delegated == LaunchResult.Success)
                {
                    App.LogInfo($"ShellExecuteEx could not resolve '{file}' (win32 error {code}); Explorer opened it instead");
                    return LaunchResult.Success;
                }
            }

            App.Log("ShellExecuteEx", new Exception($"'{file}' (verb={verb ?? "open"}) failed with win32 error {code}"));
            return LaunchResult.Failed;
        }

        return LaunchResult.Success;
    }

    private static LaunchResult StartProcess(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
            return LaunchResult.Success;
        }
        catch (Exception ex)
        {
            App.Log("Process.Start", ex);
            return LaunchResult.Failed;
        }
    }

    private static string Quote(string path) => $"\"{path}\"";

    /// <summary>
    /// Whether a target names something with a scheme rather than a file: <c>ms-settings:yourinfo</c>
    /// rather than <c>C:\Windows</c>. A drive letter is not one, and neither is one of the shell's own
    /// <c>::</c> items, which ShellExecuteEx resolves by itself.
    /// </summary>
    private static bool IsScheme(string target)
    {
        var colon = target.IndexOf(':');

        // "x:" is a drive, so a scheme has at least two characters and something after the colon.
        if (colon < 2 || colon == target.Length - 1)
        {
            return false;
        }

        for (var i = 0; i < colon; i++)
        {
            if (!char.IsLetterOrDigit(target[i]) && target[i] != '+' && target[i] != '-' && target[i] != '.')
            {
                return false;
            }
        }

        return true;
    }

    public static void ShowError(string message)
    {
        MessageBox.Show(message, "DevShell", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
