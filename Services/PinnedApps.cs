using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using DevShell.Interop;

namespace DevShell.Services;

internal sealed record PinnedApp(string Name, string ShortcutPath, ImageSource? Icon, bool IsDuplicate)
{
    public string ToolTip => $"{Name}\n{ShortcutPath}";
}

/// <summary>
/// Reads - and edits - the programs Windows itself pins to the taskbar. A pin is a shortcut in the
/// same folder Explorer keeps its own, so writing one there is all it takes for the program to be
/// pinned: DevShell reloads the folder, and Explorer picks the change up when it rebuilds its bar.
/// </summary>
internal static partial class PinnedApps
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

    /// <summary>
    /// The pinned programs, in the order the bar shows them: the order remembered in the config
    /// first, then anything new - a fresh pin, or a file Explorer added while DevShell ran - in the
    /// folder's own order.
    /// </summary>
    /// <param name="order">Shortcut file names in the order to show them.</param>
    public static IReadOnlyList<PinnedApp> Load(IReadOnlyList<string>? order = null)
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

        if (order is { Count: > 0 })
        {
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < order.Count; i++)
            {
                rank.TryAdd(order[i], i);
            }

            return apps
                .OrderBy(app => rank.TryGetValue(Path.GetFileName(app.ShortcutPath), out var index) ? index : int.MaxValue)
                .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        return apps;
    }

    public static LaunchResult Launch(PinnedApp app) => ShellActions.Open(app.ShortcutPath);

    public static LaunchResult LaunchAsAdministrator(PinnedApp app, out string? error)
        => ShellActions.OpenAsAdministrator(app.ShortcutPath, out error);

    public static LaunchResult OpenLocation(PinnedApp app) => ShellActions.OpenFileLocation(app.ShortcutPath);

    /// <summary>
    /// Pins a program: a shortcut from the Start menu is copied into the pin folder as it stands, so
    /// its arguments, icon and working directory survive; anything else - an .exe dragged from a
    /// folder, a document - gets a fresh shortcut written for it.
    /// </summary>
    /// <param name="sourcePath">The .lnk, .url, .exe or file to pin.</param>
    /// <param name="shortcutPath">Where the new pin was written.</param>
    /// <param name="error">Why it failed, when it did.</param>
    public static bool Pin(string sourcePath, out string shortcutPath, out string? error)
    {
        shortcutPath = string.Empty;
        error = null;

        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                error = Strings.T("Pin_MissingSource");
                return false;
            }

            Directory.CreateDirectory(Folder);

            var extension = Path.GetExtension(sourcePath);
            shortcutPath = UniqueShortcutPath(Path.GetFileNameWithoutExtension(sourcePath));

            if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourcePath, shortcutPath);
            }
            else
            {
                CreateShortcut(sourcePath, shortcutPath);
            }

            App.LogInfo($"Pinned {sourcePath} -> {shortcutPath}");
            return true;
        }
        catch (Exception ex)
        {
            App.Log("PinnedApps.Pin", ex);
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Unpins a program by deleting its shortcut. Only files inside the pin folder are ever removed,
    /// so a stale or hand-edited entry cannot point the delete somewhere else.
    /// </summary>
    public static bool Unpin(PinnedApp app, out string? error)
    {
        error = null;

        try
        {
            var full = Path.GetFullPath(app.ShortcutPath);
            var root = Path.GetFullPath(Folder) + Path.DirectorySeparatorChar;

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                error = Strings.T("Pin_NotAPin");
                return false;
            }

            if (File.Exists(full))
            {
                File.Delete(full);
            }

            App.LogInfo($"Unpinned {full}");
            return true;
        }
        catch (Exception ex)
        {
            App.Log("PinnedApps.Unpin", ex);
            error = ex.Message;
            return false;
        }
    }

    /// <summary>"Name.lnk", or "Name (2).lnk" when that one is taken - the naming Windows uses.</summary>
    private static string UniqueShortcutPath(string name)
    {
        var safe = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        if (safe.Length == 0)
        {
            safe = "Shortcut";
        }

        var candidate = Path.Combine(Folder, safe + ".lnk");
        var counter = 2;

        while (File.Exists(candidate))
        {
            candidate = Path.Combine(Folder, $"{safe} ({counter++}).lnk");
        }

        return candidate;
    }

    /// <summary>Writes a shortcut with the shell's own link object, so Windows treats it as any other.</summary>
    private static void CreateShortcut(string targetPath, string shortcutPath)
    {
        var link = (IShellLinkW)new ShellLink();

        try
        {
            link.SetPath(targetPath);

            var folder = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(folder))
            {
                link.SetWorkingDirectory(folder);
            }

            if (Path.GetExtension(targetPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                link.SetIconLocation(targetPath, 0);
            }

            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>"BeamNG.drive (2).lnk" is the same app as "BeamNG.drive.lnk".</summary>
    [GeneratedRegex(@"\s*\(\d+\)$")]
    private static partial Regex DuplicateSuffix();

    // ---- the shell link object -------------------------------------------

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
