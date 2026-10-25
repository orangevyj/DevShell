using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DevShell.Interop;

/// <summary>
/// Icon lookup through the shell, so shortcuts, executables and document types all
/// look exactly like they do in Explorer. Results are cached and frozen: they are
/// produced on worker threads while searching and consumed on the UI thread.
/// </summary>
internal static class ShellIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? ForPath(string path) => Get("path:" + path, () => Load(path, isDirectory: false));

    public static ImageSource? ForDirectory(string path) => Get("dir:" + path, () => Load(path, isDirectory: true));

    /// <summary>Shell icon for a file type, without touching the file itself.</summary>
    public static ImageSource? ForExtension(string extension)
    {
        var ext = string.IsNullOrWhiteSpace(extension) ? ".dat" : extension;
        return Get("ext:" + ext, () => Load("DevShell" + ext, isDirectory: false, useFileAttributes: true));
    }

    public static ImageSource? ForEntry(string path, bool isDirectory)
        => isDirectory ? ForDirectory(path) : ForPath(path);

    /// <summary>
    /// The 48px icon the desktop uses. Comes from the shell's extra-large image list so
    /// alpha, shortcut targets, .url files and folder desktop.ini icons all match Explorer.
    /// Falls back to the 32px SHGFI_LARGEICON icon when the image list is unavailable.
    /// </summary>
    public static ImageSource? ForDesktopItem(string path, bool isDirectory)
        => Get("desk:" + path, () => LoadFromImageList(path, isDirectory) ?? Load(path, isDirectory));

    private static ImageSource? LoadFromImageList(string path, bool isDirectory)
    {
        try
        {
            var info = default(NativeMethods.SHFILEINFO);
            var attributes = isDirectory
                ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY
                : NativeMethods.FILE_ATTRIBUTE_NORMAL;

            var result = NativeMethods.SHGetFileInfo(
                path, attributes, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
                NativeMethods.SHGFI_SYSICONINDEX);

            if (result == IntPtr.Zero)
            {
                return null;
            }

            return FromImageListIndex(info.iIcon);
        }
        catch (Exception ex)
        {
            App.Log("ShellIcons.LoadFromImageList", ex);
            return null;
        }
    }

    /// <summary>The shell's extra-large image list holds the 48px icons the desktop draws.</summary>
    private static ImageSource? FromImageListIndex(int index)
    {
        var iid = NativeMethods.IID_IImageList;
        if (NativeMethods.SHGetImageList(NativeMethods.SHIL_EXTRALARGE, ref iid, out var imageList) != 0
            || imageList == IntPtr.Zero)
        {
            return null;
        }

        var hIcon = NativeMethods.ImageList_GetIcon(imageList, index, NativeMethods.ILD_TRANSPARENT);
        if (hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// The 48px icon for a shell namespace item such as the Recycle Bin. Namespace items have no
    /// path, so SHGetFileInfo cannot answer for them by name - but it can for the item's PIDL,
    /// and going through the shell's own image list is also what makes a state-dependent icon
    /// (empty versus full Recycle Bin) come out the way Explorer draws it.
    /// </summary>
    public static ImageSource? ForNamespaceItem(string parsingName)
        => Get("ns:" + parsingName, () => LoadFromNamespacePidl(parsingName));

    private static ImageSource? LoadFromNamespacePidl(string parsingName)
    {
        try
        {
            var hr = NativeMethods.SHParseDisplayName(parsingName, IntPtr.Zero, out var pidl, 0, out _);
            if (hr != 0 || pidl == IntPtr.Zero)
            {
                App.LogInfo($"ShellIcons: SHParseDisplayName failed (0x{hr:X8}) for '{parsingName}'");
                return null;
            }

            try
            {
                var info = default(NativeMethods.SHFILEINFO);
                var flags = NativeMethods.SHGFI_PIDL | NativeMethods.SHGFI_SYSICONINDEX;
                var result = NativeMethods.SHGetFileInfoPidl(
                    pidl, 0, ref info,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHFILEINFO>(), flags);

                if (result == IntPtr.Zero)
                {
                    App.LogInfo($"ShellIcons: no icon index for '{parsingName}'");
                    return null;
                }

                return FromImageListIndex(info.iIcon);
            }
            finally
            {
                NativeMethods.ILFree(pidl);
            }
        }
        catch (Exception ex)
        {
            App.Log("ShellIcons.LoadFromNamespacePidl", ex);
            return null;
        }
    }

    /// <summary>
    /// Raw 48px icon from an explicit icon resource, which is how .url shortcuts point at
    /// their own image when they do not use a document icon.
    /// </summary>
    public static ImageSource? FromIconFile(string file, int index)
        => Get($"iconfile:{file}#{index}", () =>
        {
            try
            {
                var large = new IntPtr[1];
                var extracted = NativeMethods.ExtractIconEx(file, index, large, null, 1);
                if (extracted == 0 || large[0] == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(
                        large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    NativeMethods.DestroyIcon(large[0]);
                }
            }
            catch (Exception ex)
            {
                App.Log("ShellIcons.FromIconFile", ex);
                return null;
            }
        });

    /// <summary>
    /// Display name the shell would show for a path. <paramref name="virtualItem"/> is for CLSID
    /// paths like the Recycle Bin, which must be resolved against the real shell namespace.
    /// </summary>
    public static string? DisplayName(string path, bool virtualItem = false)
    {
        // A "::{CLSID}" path is not a file and SHGetFileInfo answers 0 for it, so namespace items
        // are named through IShellItem - the API Explorer itself goes through. That is also what
        // makes the name come out in the machine's own language ("Корзина", not "Recycle Bin").
        if (virtualItem)
        {
            return NamespaceName(path);
        }

        try
        {
            var info = default(NativeMethods.SHFILEINFO);
            var flags = NativeMethods.SHGFI_DISPLAYNAME | NativeMethods.SHGFI_USEFILEATTRIBUTES;

            var result = NativeMethods.SHGetFileInfo(
                path, NativeMethods.FILE_ATTRIBUTE_NORMAL, ref info,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
                flags);

            return result == IntPtr.Zero || string.IsNullOrWhiteSpace(info.szDisplayName)
                ? null
                : info.szDisplayName;
        }
        catch (Exception ex)
        {
            App.Log("ShellIcons.DisplayName", ex);
            return null;
        }
    }

    private static string? NamespaceName(string parsingName)
    {
        try
        {
            var iid = NativeMethods.IID_IShellItem;
            var hr = NativeMethods.SHCreateItemFromParsingName(
                parsingName, IntPtr.Zero, ref iid, out var item);

            if (hr != 0 || item is null)
            {
                App.LogInfo($"ShellIcons: SHCreateItemFromParsingName failed (0x{hr:X8}) for '{parsingName}'");
                return null;
            }

            try
            {
                var nameHr = item.GetDisplayName(NativeMethods.SIGDN_NORMALDISPLAY, out var name);
                if (nameHr != 0 || string.IsNullOrWhiteSpace(name))
                {
                    App.LogInfo($"ShellIcons: GetDisplayName failed (0x{nameHr:X8}) for '{parsingName}'");
                    return null;
                }

                return name;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(item);
            }
        }
        catch (Exception ex)
        {
            App.Log("ShellIcons.NamespaceName", ex);
            return null;
        }
    }

    private static ImageSource? Get(string key, Func<ImageSource?> factory)
        => Cache.GetOrAdd(key, _ => factory());

    private static ImageSource? Load(string path, bool isDirectory, bool useFileAttributes = false)
    {
        var info = default(NativeMethods.SHFILEINFO);
        var attributes = isDirectory
            ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY
            : NativeMethods.FILE_ATTRIBUTE_NORMAL;

        var flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON;
        if (useFileAttributes)
        {
            flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;
        }

        var result = NativeMethods.SHGetFileInfo(
            path, attributes, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHFILEINFO>(), flags);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            App.Log("CreateBitmapSourceFromHIcon", ex);
            return null;
        }
        finally
        {
            NativeMethods.DestroyIcon(info.hIcon);
        }
    }

    /// <summary>Legacy glyph fallback when the icon font is missing.</summary>
    public static ImageSource? GenericFileIcon => ForExtension(".txt");
}
