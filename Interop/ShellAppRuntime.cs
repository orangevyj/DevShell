using System.Diagnostics;
using System.IO;

namespace DevShell.Interop;

/// <summary>
/// The piece of Explorer that modern Windows apps need, kept running when DevShell is the shell.
/// </summary>
/// <remarks>
/// A packaged application - Settings, Calculator, any Store app - is not started the way a Win32
/// program is. It has to be activated, and the process that does the activating has always been
/// explorer.exe: it is the "metro host" the activation is handed to. Replace Explorer and nothing
/// hosts it any more, which is why Settings and every other packaged app either refuses to start or
/// waits forever for a launch that never happens - and why the same apps work again the moment
/// Explorer is put back.
///
/// Windows 11 (build 25252 and later) split that host out into a program of its own,
/// ShellAppRuntime.exe, and that one runs with no explorer.exe in sight. Starting it is what puts
/// packaged-app activation back on a DevShell session. It brings the "Program Manager" (Progman)
/// window with it, which is the window <see cref="DesktopPlacement"/> pins the desktop layer above.
/// </remarks>
internal static class ShellAppRuntime
{
    private const string ProcessName = "ShellAppRuntime";
    private const string ExecutableName = "ShellAppRuntime.exe";

    /// <summary>
    /// How often a host that is not up is asked for again while the bar runs. Explorer keeps its host
    /// alive for as long as it is the shell; DevShell has to do the same, or a host that dies takes
    /// Settings and every other Store app with it until the next sign-in.
    /// </summary>
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(15);

    /// <summary>A machine without the host at all is not asked again on every tick.</summary>
    private static bool _absent;

    private static DateTime _lastCheck = DateTime.MinValue;

    /// <summary>True while the packaged-app host is up, whoever started it.</summary>
    public static bool IsRunning()
    {
        try
        {
            return Process.GetProcessesByName(ProcessName).Length > 0;
        }
        catch (Exception ex)
        {
            App.Log("ShellAppRuntime.IsRunning", ex);
            return false;
        }
    }

    /// <summary>
    /// Starts the host if this Windows has one and it is not already up. Called where DevShell is the
    /// shell: a session that still runs Explorer has had the host all along. Asked for again while the
    /// bar runs, so a host that has died is brought back.
    /// </summary>
    public static void EnsureRunning()
    {
        // The clock is looked at before the process list: this is called from the bar's own tick, and
        // almost every call has to be a comparison rather than an enumeration of every process.
        var now = DateTime.UtcNow;

        if (_absent || now - _lastCheck < RecheckInterval)
        {
            return;
        }

        _lastCheck = now;

        if (IsRunning())
        {
            return;
        }

        try
        {
            var path = Path.Combine(Environment.SystemDirectory, ExecutableName);

            if (!File.Exists(path))
            {
                // Windows 10 and the first Windows 11 builds keep the host inside Explorer, so there
                // is nothing to start: packaged apps stay as broken as they are for any other
                // replacement shell. Said once so the log explains the symptom.
                _absent = true;
                App.LogInfo($"ShellAppRuntime: {ExecutableName} is not on this Windows, so packaged apps " +
                            "have no host while Explorer is not the shell");
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true });
            App.LogInfo("ShellAppRuntime: the packaged-app host is up - Explorer is not here to provide it");
        }
        catch (Exception ex)
        {
            App.Log("ShellAppRuntime.EnsureRunning", ex);
        }
    }
}
