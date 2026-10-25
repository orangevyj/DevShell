using Microsoft.Win32;
using DevShell.Interop;

namespace DevShell.Services;

/// <summary>
/// The two ways DevShell can live on a machine, and the registry values that decide between them.
/// </summary>
/// <remarks>
/// Both choices are per-user and need no administrator: the "Shell" value under the current user's
/// own Winlogon key replaces Explorer for that account only, and a "Run" entry starts the bar with
/// the session while Explorer keeps the desktop. Windows reads the per-user Winlogon key before the
/// machine-wide one, which is what makes this possible without elevation.
///
/// Nothing here is a one-way door: each choice clears what the other one writes, and
/// <see cref="RestoreExplorer"/> puts the account back exactly as it was.
/// </remarks>
internal static class WindowsIntegration
{
    private const string WinlogonKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ShellValue = "Shell";
    private const string RunValue = "DevShell";

    /// <summary>The executable that is running right now, quoted as the shell value wants it.</summary>
    public static string ExecutablePath => Environment.ProcessPath ?? string.Empty;

    /// <summary>Whether Windows is set to start DevShell instead of Explorer for this account.</summary>
    public static bool IsDevShellTheShell()
    {
        var shell = ReadShell();
        return shell is not null && PathsMatch(shell, ExecutablePath);
    }

    /// <summary>Whether this account is set to start the bar alongside Explorer.</summary>
    public static bool StartsWithTheSession() => ReadRun() is not null;

    /// <summary>
    /// The shell Windows will start for this account, or null when the account has not been changed
    /// and Explorer is still what comes up.
    /// </summary>
    public static string? ReadShell()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(WinlogonKey);
            var value = key?.GetValue(ShellValue) as string;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception ex)
        {
            App.Log("WindowsIntegration.ReadShell", ex);
            return null;
        }
    }

    /// <summary>DevShell takes Explorer's place: started as the shell, and not also from Run.</summary>
    public static void UseAsShell()
    {
        WriteShell(ExecutablePath);
        WriteRun(null);

        // Explorer is what hosts packaged-app activation, so a session that is about to run without
        // it needs the host provided - see ShellAppRuntime.
        ShellAppRuntime.EnsureRunning();
    }

    /// <summary>Explorer keeps the desktop and DevShell's bar starts with the session instead.</summary>
    public static void RunAlongside()
    {
        WriteShell(null);
        WriteRun(ExecutablePath);
    }

    /// <summary>Back to plain Explorer: neither the shell value nor a Run entry is left behind.</summary>
    public static void RestoreExplorer()
    {
        WriteShell(null);
        WriteRun(null);
    }

    /// <summary>
    /// Puts the desktop layer where the mode calls for it. DevShell draws the wallpaper and puts
    /// icons on it when it is the shell, because Explorer's desktop is not there to do it; beside
    /// Explorer it stays out of the way, since two sets of desktop icons on one wallpaper is one
    /// set too many. Returns true when the config changed and the surface should catch up.
    /// </summary>
    public static bool ApplyDesktopLayerMode(Config config)
    {
        var wanted = IsDevShellTheShell();

        if (config.ShowDesktopLayer == wanted)
        {
            return false;
        }

        config.ShowDesktopLayer = wanted;
        config.Save();
        return true;
    }

    // ---- the registry itself --------------------------------------------

    private static void WriteShell(string? value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(WinlogonKey, true);

            if (key is null)
            {
                App.LogInfo("WindowsIntegration: the Winlogon key could not be opened for writing");
                return;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                // Only take out a value that points at DevShell: an account set to some other shell
                // keeps it, so putting Explorer back never tramples somebody else's setup.
                var current = key.GetValue(ShellValue) as string;

                if (current is not null && PathsMatch(current, ExecutablePath))
                {
                    key.DeleteValue(ShellValue, false);
                    App.LogInfo("WindowsIntegration: Explorer is the shell again");
                }

                return;
            }

            key.SetValue(ShellValue, Quote(value), RegistryValueKind.String);
            App.LogInfo($"WindowsIntegration: Windows will start {value} as the shell");
        }
        catch (Exception ex)
        {
            App.Log("WindowsIntegration.WriteShell", ex);
        }
    }

    private static string? ReadRun()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) as string;
        }
        catch (Exception ex)
        {
            App.Log("WindowsIntegration.ReadRun", ex);
            return null;
        }
    }

    private static void WriteRun(string? value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);

            if (key is null)
            {
                App.LogInfo("WindowsIntegration: the Run key could not be opened for writing");
                return;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                key.DeleteValue(RunValue, false);
                return;
            }

            key.SetValue(RunValue, Quote(value), RegistryValueKind.String);
            App.LogInfo($"WindowsIntegration: {value} starts with the session");
        }
        catch (Exception ex)
        {
            App.Log("WindowsIntegration.WriteRun", ex);
        }
    }

    /// <summary>Registry paths are often quoted and often not, so compare them without the quotes.</summary>
    private static bool PathsMatch(string left, string right)
        => string.Equals(Unquote(left), Unquote(right), StringComparison.OrdinalIgnoreCase);

    private static string Unquote(string value) => value.Trim().Trim('"');

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
