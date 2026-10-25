using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using DevShell.Interop;

namespace DevShell.Services;

internal sealed record PinnedApp(string Name, string ShortcutPath, ImageSource? Icon, bool IsDuplicate)
{
    public string ToolTip => $"{Name}\n{ShortcutPath}";
}

/// <summary>Reads the programs Windows itself pins to the taskbar.</summary>
internal static partial class PinnedApps
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

    public static IReadOnlyList<PinnedApp> Load()
    {
        var apps = new List<PinnedApp>();

        try
        {
            if (!Directory.Exists(Folder))
            {
                App.LogInfo($"Pinned taskbar folder not found: {Folder}");
                return apps;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(Folder, "*.lnk").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var key = DuplicateSuffix().Replace(name, string.Empty).Trim();
                var isDuplicate = !seen.Add(key);

                apps.Add(new PinnedApp(name, file, ShellIcons.ForPath(file), isDuplicate));
            }
        }
        catch (Exception ex)
        {
            App.Log("PinnedApps.Load", ex);
        }

        return apps;
    }

    public static LaunchResult Launch(PinnedApp app) => ShellActions.Open(app.ShortcutPath);

    public static LaunchResult LaunchAsAdministrator(PinnedApp app, out string? error)
        => ShellActions.OpenAsAdministrator(app.ShortcutPath, out error);

    public static LaunchResult OpenLocation(PinnedApp app) => ShellActions.OpenFileLocation(app.ShortcutPath);

    /// <summary>"BeamNG.drive (2).lnk" is the same app as "BeamNG.drive.lnk".</summary>
    [GeneratedRegex(@"\s*\(\d+\)$")]
    private static partial Regex DuplicateSuffix();
}
