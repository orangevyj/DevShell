namespace DevShell.Interop;

/// <summary>
/// What the Start menu's power button offers. All of it goes through the system's own tools
/// (shutdown.exe and powrprof) rather than an API, so Windows runs its normal sequence with its
/// own checks instead of DevShell inventing a shortened one.
/// </summary>
internal static class PowerActions
{
    /// <summary>Suspend the machine. 0,1,0 = suspend, force, no event; the call Windows itself makes.</summary>
    public static LaunchResult Sleep() => ShellActions.Run("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");

    public static LaunchResult ShutDown() => ShellActions.Run("shutdown.exe", "/s /t 0");

    public static LaunchResult Restart() => ShellActions.Run("shutdown.exe", "/r /t 0");

    public static LaunchResult SignOut() => ShellActions.Run("shutdown.exe", "/l");
}
