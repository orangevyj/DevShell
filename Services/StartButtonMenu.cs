using System.IO;
using System.Windows.Media;
using DevShell.Interop;

namespace DevShell.Services;

/// <summary>One entry of the menu the Start button puts up when it is right-clicked.</summary>
internal sealed record StartButtonItem(string Name, string ShortcutPath, bool IsAdministrator, ImageSource? Icon)
{
    public string ToolTip => $"{Name}\n{ShortcutPath}";
}

/// <summary>
/// The menu Windows has always shown when the Start button is right-clicked - Device Manager, System,
/// Task Manager and the rest. Windows builds it out of the shortcut folders under
/// <c>%LOCALAPPDATA%\Microsoft\Windows\WinX</c>, one folder per group, and DevShell reads those same
/// folders: the entries, the names (the shell's own, so they are localized the way Explorer's are)
/// and the icons are the ones the real menu would have shown, and anything the user adds to or
/// removes from those folders turns up on the button here exactly as it does there.
/// </summary>
/// <remarks>
/// The groups are read in reverse: the menu's bottom group is <c>Group1</c> and its top group is the
/// highest one, and inside a group the entries count down as the <c>NN - </c> prefixes in the file
/// names go up. That is why "10 - AppsAndFeatures.lnk" sits at the top of the menu and
/// "1 - Desktop.lnk" at the bottom, and reading the folders this way keeps a user's own additions in
/// the place Windows would have put them.
/// </remarks>
internal static class StartButtonMenu
{
    /// <summary>The user's own copy of the folders, which is the one Windows gives the menu.</summary>
    private static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "WinX");

    /// <summary>
    /// The template Windows copies into a new profile. It is only read when the user's own folders
    /// hold no shortcuts at all, so a profile that has never had a Win+X menu still gets one.
    /// </summary>
    private static string Template { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "..", "Default", "AppData",
        "Local", "Microsoft", "Windows", "WinX");

    /// <summary>
    /// The menu's groups, top group first, each already in the order the menu shows its entries.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<StartButtonItem>> Load()
    {
        var groups = Read(Root);

        if (groups.Count == 0)
        {
            groups = Read(Path.GetFullPath(Template));
        }

        return groups;
    }

    /// <summary>Starts a menu entry the way Windows starts it, elevated if the shortcut asks for it.</summary>
    public static LaunchResult Launch(StartButtonItem item)
        => item.IsAdministrator
            ? ShellActions.OpenAsAdministrator(item.ShortcutPath, out _)
            : ShellActions.Open(item.ShortcutPath);

    private static List<IReadOnlyList<StartButtonItem>> Read(string root)
    {
        var groups = new List<IReadOnlyList<StartButtonItem>>();

        try
        {
            if (!Directory.Exists(root))
            {
                return groups;
            }

            // Windows' own groups sit in Group1, Group2, Group3 and are shown back to front.
            var folders = Directory.EnumerateDirectories(root)
                .OrderByDescending(folder => folder, StringComparer.OrdinalIgnoreCase);

            foreach (var folder in folders)
            {
                var items = new List<StartButtonItem>();

                foreach (var file in Directory.EnumerateFiles(folder, "*.lnk")
                             .OrderByDescending(file => file, StringComparer.OrdinalIgnoreCase))
                {
                    var administrator = ShortcutFile.IsElevated(file);
                    items.Add(new StartButtonItem(
                        NameOf(file, administrator), file, administrator, ShellIcons.ForPath(file)));
                }

                if (items.Count > 0)
                {
                    groups.Add(items);
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"StartButtonMenu.Read({root})", ex);
        }

        return groups;
    }

    /// <summary>
    /// The name the shell gives a shortcut, with " (Admin)" for the entries that were saved to run
    /// elevated - which is how the real menu tells its two PowerShell entries apart. The file name
    /// alone would spell those as "05 - Device Manager.lnk" style abbreviations rather than the
    /// names Explorer shows.
    /// </summary>
    private static string NameOf(string shortcut, bool administrator)
    {
        var name = ShellStrings.DisplayName(shortcut);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileNameWithoutExtension(shortcut);
        }

        return administrator ? $"{name} (Admin)" : name;
    }
}
