using System.IO;
using System.Windows.Media;
using DevShell.Interop;

namespace DevShell.Services;

public sealed record StartApp(string Name, string ShortcutPath, string Group, ImageSource? Icon)
{
    public string ToolTip => $"{Name}\n{ShortcutPath}";

    /// <summary>The folder path as it reads under a heading: "Accessories › System Tools".</summary>
    public string Folder => Group.Length == 0 ? string.Empty : Group.Replace("\\", " › ");

    /// <summary>Programs are not headings; the list binds one container style to both row kinds.</summary>
    public bool IsGroupRow => false;
}

/// <summary>
/// The programs Windows itself lists in its Start menu: every shortcut under the per-user and the
/// machine-wide "Start Menu\Programs" folders, kept in the folders Windows puts them in, so the menu
/// groups them the way Explorer does instead of showing one long alphabetical list.
/// </summary>
internal static class StartMenuApps
{
    private static readonly string[] Roots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
    };

    public static IReadOnlyList<StartApp> Load()
    {
        var apps = new List<StartApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in Roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var extension = Path.GetExtension(file);
                    if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var name = Path.GetFileNameWithoutExtension(file);
                    if (!seen.Add(name))
                    {
                        // The machine-wide copy of a program that also lives in the user's own menu.
                        continue;
                    }

                    apps.Add(new StartApp(name, file, GroupOf(root, file), ShellIcons.ForPath(file)));
                }
            }
            catch (Exception ex)
            {
                App.Log("StartMenuApps.Load", ex);
            }
        }

        return apps
            .OrderBy(app => app.Group.Length == 0 ? 0 : 1)
            .ThenBy(app => app.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static LaunchResult Launch(StartApp app) => ShellActions.Open(app.ShortcutPath);

    public static LaunchResult LaunchAsAdministrator(StartApp app, out string? error)
        => ShellActions.OpenAsAdministrator(app.ShortcutPath, out error);

    public static LaunchResult OpenLocation(StartApp app) => ShellActions.OpenFileLocation(app.ShortcutPath);

    /// <summary>
    /// The folder a shortcut sits in, relative to the Start Menu root. A folder is exactly how
    /// Windows groups programs, and root-level programs come back empty.
    /// </summary>
    private static string GroupOf(string root, string file)
    {
        var folder = Path.GetDirectoryName(file) ?? root;
        var relative = Path.GetRelativePath(root, folder);
        return relative == "." ? string.Empty : relative;
    }
}
