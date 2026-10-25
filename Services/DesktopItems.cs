using System.IO;
using System.Windows.Media;
using DevShell.Interop;
using Microsoft.Win32;

namespace DevShell.Services;

internal enum DesktopItemKind
{
    /// <summary>A shell namespace object that has no path, such as the Recycle Bin.</summary>
    Virtual,
    Folder,
    File,
}

/// <summary>
/// One icon on the desktop, already resolved to the name and 48px icon Explorer would draw.
/// </summary>
internal sealed class DesktopItem
{
    public required string DisplayName { get; init; }

    /// <summary>Full path, or null for virtual items.</summary>
    public string? Path { get; init; }

    public required DesktopItemKind Kind { get; init; }

    /// <summary>What the shell gets on double-click: a real path, or a CLSID path for virtual items.</summary>
    public required string OpenTarget { get; init; }

    public ImageSource? Icon { get; init; }

    public bool IsHidden { get; init; }

    /// <summary>True when the item is a path we can act on with Explorer verbs.</summary>
    public bool HasPath => !string.IsNullOrEmpty(Path);

    /// <summary>Tooltip: path when there is one, otherwise whatever the shell calls the item.</summary>
    public string Tooltip => Path ?? DisplayName;
}

/// <summary>
/// Builds the desktop's contents the same way the shell does: the user's Desktop folder plus the
/// shared Public Desktop, with desktop.ini supplying localized labels and custom icons, and the
/// Recycle Bin in front because it is a namespace object rather than a file.
/// </summary>
internal static class DesktopItems
{
    private const string RecycleBinClsid = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    /// <summary>Files the shell itself stores on the desktop and never shows as icons.</summary>
    private static readonly string[] IgnoredFileNames =
    {
        "desktop.ini",
        "thumbs.db",
        "iconcache.db",
    };

    /// <summary>
    /// Everything the desktop shows, in the order the shell shows it. <paramref name="showExtensions"/>
    /// decides whether a file is labelled 'report.txt' or 'report'.
    /// </summary>
    public static List<DesktopItem> Enumerate(
        bool includePublic, bool includeRecycleBin, bool showHidden, bool showExtensions)
    {
        var items = new List<DesktopItem>();

        if (includeRecycleBin)
        {
            items.Add(new DesktopItem
            {
                DisplayName = ShellIcons.DisplayName(RecycleBinClsid, virtualItem: true) ?? "Recycle Bin",
                Kind = DesktopItemKind.Virtual,
                OpenTarget = RecycleBinClsid,
                Icon = ShellIcons.ForNamespaceItem(RecycleBinClsid),
            });
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in SourceFolders(includePublic))
        {
            foreach (var item in EnumerateFolder(folder, showHidden, showExtensions))
            {
                // The user's own desktop wins over the shared one when both define a name.
                if (seen.Add(Path.GetFileName(item.Path!)))
                {
                    items.Add(item);
                }
            }
        }

        items.Sort(Compare);
        return items;
    }

    /// <summary>
    /// User desktop first (its entries shadow Public ones), then the shared desktop.
    /// </summary>
    private static IEnumerable<string> SourceFolders(bool includePublic)
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(user))
        {
            yield return user;
        }

        if (!includePublic)
        {
            yield break;
        }

        var shared = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrEmpty(shared))
        {
            yield return shared;
        }
    }

    private static IEnumerable<DesktopItem> EnumerateFolder(
        string folder, bool showHidden, bool showExtensions)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(folder);
        }
        catch (Exception ex)
        {
            App.Log($"DesktopItems.EnumerateFolder({folder})", ex);
            yield break;
        }

        // One desktop.ini per folder holds the localized names for everything inside it.
        var folderIni = IniFile.Read(Path.Combine(folder, "desktop.ini"));

        foreach (var path in entries)
        {
            var fileName = Path.GetFileName(path);
            if (IsIgnored(fileName))
            {
                continue;
            }

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception ex)
            {
                // A file that vanished mid-enumeration is normal, not worth a log entry.
                App.LogInfo($"DesktopItems: skipping '{path}' ({ex.GetType().Name})");
                continue;
            }

            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var isHidden = attributes.HasFlag(FileAttributes.Hidden);

            if (isHidden && !showHidden)
            {
                continue;
            }

            yield return new DesktopItem
            {
                DisplayName = DisplayNameFor(path, fileName, isDirectory, folderIni, showExtensions),
                Path = path,
                Kind = isDirectory ? DesktopItemKind.Folder : DesktopItemKind.File,
                OpenTarget = path,
                Icon = IconFor(path, fileName, isDirectory),
                IsHidden = isHidden,
            };
        }
    }

    private static bool IsIgnored(string fileName)
    {
        if (IgnoredFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // Explorer hides its own scratch files with these extensions.
        return fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    private static string DisplayNameFor(
        string path, string fileName, bool isDirectory, IniFile? folderIni, bool showExtensions)
    {
        // [LocalizedFileNames] is keyed by the real file name, value an indirect string.
        var localized = ShellStrings.Resolve(folderIni?.Get("LocalizedFileNames", fileName));
        if (localized is not null)
        {
            return localized;
        }

        if (isDirectory)
        {
            // A folder can rename itself through its own desktop.ini.
            var own = IniFile.Read(Path.Combine(path, "desktop.ini"));
            var renamed = ShellStrings.Resolve(own?.Get(".ShellClassInfo", "LocalizedResourceName"));
            return renamed ?? fileName;
        }

        var extension = Path.GetExtension(fileName);

        // Shortcuts are always shown without their extension, whatever the folder options say.
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".url", StringComparison.OrdinalIgnoreCase)
            || !showExtensions)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            return stem.Length > 0 ? stem : fileName;
        }

        return fileName;
    }

    private static ImageSource? IconFor(string path, string fileName, bool isDirectory)
    {
        // .url shortcuts carry their own icon resource; the shell image list does not read it.
        if (fileName.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            var url = IniFile.Read(path);
            var iconFile = url?.Get("InternetShortcut", "IconFile");
            if (!string.IsNullOrWhiteSpace(iconFile))
            {
                var expanded = Environment.ExpandEnvironmentVariables(iconFile.Trim().Trim('"'));
                var index = int.TryParse(url?.Get("InternetShortcut", "IconIndex"), out var parsed) ? parsed : 0;
                var custom = ShellIcons.FromIconFile(expanded, index);
                if (custom is not null)
                {
                    return custom;
                }
            }
        }

        // Folders with a desktop.ini IconResource resolve through the shell image list on their own.
        return ShellIcons.ForDesktopItem(path, isDirectory);
    }

    /// <summary>
    /// Explorer's "hide extensions for known file types" setting, which decides whether document
    /// icons show 'report.txt' or 'report'. This is only where a new config starts out; from then on
    /// the desktop follows <see cref="Config.DesktopShowFileExtensions"/>, set in the settings flyout.
    /// </summary>
    internal static bool WindowsShowsFileExtensions()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");

            // The key is HideFileExt, so 0 means extensions are shown.
            return key?.GetValue("HideFileExt") is int hide ? hide == 0 : false;
        }
        catch (Exception ex)
        {
            App.Log("DesktopItems.WindowsShowsFileExtensions", ex);
            return false;
        }
    }

    /// <summary>Recycle Bin, then folders, then files; each group sorted the way the shell sorts.</summary>
    private static int Compare(DesktopItem left, DesktopItem right)
    {
        var byKind = Rank(left).CompareTo(Rank(right));
        if (byKind != 0)
        {
            return byKind;
        }

        return string.Compare(
            left.DisplayName, right.DisplayName, StringComparison.CurrentCultureIgnoreCase);
    }

    private static int Rank(DesktopItem item) => item.Kind switch
    {
        DesktopItemKind.Virtual => 0,
        DesktopItemKind.Folder => 1,
        _ => 2,
    };
}
