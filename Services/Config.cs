using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevShell.Services;

/// <summary>User-tweakable settings, stored next to the log in %LOCALAPPDATA%\DevShell.</summary>
internal sealed class Config
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Height of the bar in device-independent pixels.</summary>
    public double BarHeight { get; set; } = 46;

    /// <summary>Ask DWM for an acrylic backdrop. Falls back to a solid colour if unavailable.</summary>
    public bool Acrylic { get; set; } = true;

    /// <summary>
    /// How much of the bar's own tint is painted over the backdrop behind it: 1 is the bar exactly as
    /// designed, 0.15 is as see-through as the settings slider goes. At 1 nothing about the bar changes.
    /// </summary>
    public double BarOpacity { get; set; } = 1.0;

    /// <summary>Overlay = sit on top of the system taskbar. AppBar = reserve screen space.</summary>
    public string DockMode { get; set; } = "Overlay";

    public bool ShowSecondsInClock { get; set; }

    /// <summary>
    /// What the throughput monitor counts in: "Bytes" (KB/s, MB/s) or "Bits" (Kbit/s, Mbit/s).
    /// Chosen from the monitor's right-click menu; bytes is the default, which is how file sizes are
    /// quoted. The link speed in the network flyout is a property of the link and stays in bits.
    /// </summary>
    public string NetworkRateUnits { get; set; } = "Bytes";

    /// <summary>
    /// Which family of troubleshooter the network and volume buttons' right-click opens: true is the
    /// classic msdt packs (Windows 10-style, default), false is the Get Help app Windows 11 uses.
    /// Windows has begun removing the classic packs, so this is a choice rather than a guess.
    /// </summary>
    public bool UseClassicTroubleshooters { get; set; } = true;

    /// <summary>
    /// Which language the bar, its menus and its dialogs speak: "auto" follows the Windows display
    /// language, otherwise one of "en", "ru", "de", "fr" or "es". Chosen in the settings popup.
    /// </summary>
    public string Language { get; set; } = "auto";

    /// <summary>Walk every fixed/removable drive at startup (results are cached for speed).</summary>
    public bool IndexOnStartup { get; set; } = true;

    /// <summary>Reuse the cached index from the previous run instead of starting from nothing.</summary>
    public bool UseIndexCache { get; set; } = true;

    /// <summary>Upper bound so a runaway index cannot eat all memory.</summary>
    public int MaxIndexEntries { get; set; } = 1_500_000;

    /// <summary>Folder names skipped anywhere in a path; these are shell/system noise.</summary>
    public string[] SkippedFolderNames { get; set; } =
    {
        "$RECYCLE.BIN",
        "System Volume Information",
        "WinSxS",
        "Config.Msi",
        "$SysReset",
        "Recovery",
    };

    public string[] SkippedFileNames { get; set; } =
    {
        "pagefile.sys",
        "hiberfil.sys",
        "swapfile.sys",
        "DumpStack.log.tmp",
    };

    // ---- desktop layer --------------------------------------------------

    /// <summary>Draw the desktop surface (wallpaper plus icons) behind the bar.</summary>
    public bool ShowDesktopLayer { get; set; } = true;

    /// <summary>Draw desktop icons at all; false leaves just the wallpaper.</summary>
    public bool DesktopShowIcons { get; set; } = true;

    /// <summary>Include the shared Public Desktop next to the user's own.</summary>
    public bool DesktopIncludePublic { get; set; } = true;

    public bool DesktopShowRecycleBin { get; set; } = true;

    public bool DesktopShowHiddenItems { get; set; }

    /// <summary>
    /// Show 'report.txt' rather than 'report' on the desktop icons. A fresh config starts out as
    /// whatever Windows was already set to - Explorer's "hide extensions for known file types" - and
    /// from then on this is DevShell's own choice, made in the settings flyout.
    /// </summary>
    public bool DesktopShowFileExtensions { get; set; } = DesktopItems.WindowsShowsFileExtensions();

    /// <summary>Icon edge length in device-independent pixels; the desktop default is 48.</summary>
    public double DesktopIconSize { get; set; } = 48;

    /// <summary>
    /// Where the user dragged each icon, keyed by the item's path (or its shell parsing name for
    /// namespace items such as the Recycle Bin), stored as {x, y} in surface coordinates. An item
    /// with no entry here is still sitting on the auto-arrange grid.
    /// </summary>
    public Dictionary<string, double[]> DesktopPositions { get; set; } = new();

    /// <summary>Explicit wallpaper file; null means "use whatever Windows is using".</summary>
    public string? WallpaperPath { get; set; }

    [JsonIgnore]
    public static string FilePath => Path.Combine(App.DataDirectory, "config.json");

    private static Config? _shared;

    /// <summary>
    /// The process-wide settings instance. The bar and the desktop window share one object, so a
    /// change made in either is visible to the other and neither can save a stale copy over it.
    /// </summary>
    public static Config Load() => _shared ??= ReadFromDisk();

    /// <summary>Re-read the file, e.g. after the user edits it by hand.</summary>
    public static Config Reload() => _shared = ReadFromDisk();

    private static Config ReadFromDisk()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), Options);
                if (config is not null)
                {
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log("Config.Load", ex);
        }

        var fresh = new Config();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            App.Log("Config.Save", ex);
        }
    }
}
