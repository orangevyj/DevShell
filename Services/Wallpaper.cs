using System.IO;
using System.Windows.Media;
using Microsoft.Win32;

namespace DevShell.Services;

/// <summary>How Windows was told to lay the image out on the screen.</summary>
internal enum WallpaperFit
{
    Fill,
    Fit,
    Stretch,
    Center,
    Tile,
    Span,
}

/// <summary>
/// Where the desktop wallpaper comes from and how it should be drawn.
/// </summary>
/// <remarks>
/// The registry path (<c>HKCU\Control Panel\Desktop\Wallpaper</c>) is only a hint: it happily
/// keeps pointing at a file the user has since deleted. Windows itself always renders the
/// processed copy in <c>%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper</c>, so that is
/// the fallback that actually keeps the desktop looking like the desktop.
/// </remarks>
internal sealed record WallpaperInfo(string? ImagePath, WallpaperFit Fit, Color FallbackColor, string Origin)
{
    public static WallpaperInfo Detect(string? overridePath)
    {
        var fit = ReadFit();
        var color = ReadFallbackColor();

        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(overridePath);
            if (File.Exists(expanded))
            {
                return new WallpaperInfo(expanded, fit, color, "config WallpaperPath");
            }

            App.LogInfo($"Wallpaper: config WallpaperPath '{expanded}' does not exist - falling back");
        }

        var registryPath = ReadRegistryWallpaper();
        if (registryPath is not null)
        {
            return new WallpaperInfo(registryPath, fit, color, "HKCU\\Control Panel\\Desktop\\Wallpaper");
        }

        var transcoded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Themes", "TranscodedWallpaper");

        if (File.Exists(transcoded))
        {
            return new WallpaperInfo(transcoded, fit, color, "Themes\\TranscodedWallpaper (registry path was missing)");
        }

        var cached = FirstCachedWallpaper();
        if (cached is not null)
        {
            return new WallpaperInfo(cached, fit, color, "Themes\\CachedFiles");
        }

        return new WallpaperInfo(null, fit, color, "solid colour from HKCU\\Control Panel\\Colors");
    }

    private static string? ReadRegistryWallpaper()
    {
        try
        {
            var value = ReadString(@"Control Panel\Desktop", "Wallpaper");
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var expanded = Environment.ExpandEnvironmentVariables(value);
            return File.Exists(expanded) ? expanded : null;
        }
        catch (Exception ex)
        {
            App.Log("Wallpaper.ReadRegistryWallpaper", ex);
            return null;
        }
    }

    private static string? FirstCachedWallpaper()
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Themes", "CachedFiles");

            if (!Directory.Exists(folder))
            {
                return null;
            }

            return Directory.EnumerateFiles(folder)
                .FirstOrDefault(file => file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                     || file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                     || file.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            App.Log("Wallpaper.FirstCachedWallpaper", ex);
            return null;
        }
    }

    private static WallpaperFit ReadFit()
    {
        try
        {
            var style = ReadString(@"Control Panel\Desktop", "WallpaperStyle") ?? "10";
            var tiled = ReadString(@"Control Panel\Desktop", "TileWallpaper") == "1";

            return style switch
            {
                "0" => tiled ? WallpaperFit.Tile : WallpaperFit.Center,
                "2" => WallpaperFit.Stretch,
                "6" => WallpaperFit.Fit,
                "22" => WallpaperFit.Span,
                _ => WallpaperFit.Fill,
            };
        }
        catch (Exception ex)
        {
            App.Log("Wallpaper.ReadFit", ex);
            return WallpaperFit.Fill;
        }
    }

    private static Color ReadFallbackColor()
    {
        try
        {
            // "R G B" triple; this is the colour Windows falls back to when no image is set.
            var raw = ReadString(@"Control Panel\Colors", "Background");
            var parts = raw?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts is { Length: >= 3 }
                && byte.TryParse(parts[0], out var r)
                && byte.TryParse(parts[1], out var g)
                && byte.TryParse(parts[2], out var b))
            {
                return Color.FromRgb(r, g, b);
            }
        }
        catch (Exception ex)
        {
            App.Log("Wallpaper.ReadFallbackColor", ex);
        }

        return Color.FromRgb(0, 0, 0);
    }

    private static string? ReadString(string subKeyPath, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKeyPath);
        return key?.GetValue(name) as string;
    }
}
