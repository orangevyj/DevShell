using System.Diagnostics;
using System.Xml.Linq;

namespace DevShell.Services;

/// <summary>Which status button asked, and therefore which troubleshooter Windows should open.</summary>
internal enum TroubleshooterTarget
{
    Network,
    Sound,
}

/// <summary>
/// Opens one of Windows' two families of troubleshooter. The classic ones are msdt packs that
/// Windows has shipped for years; the newer ones live inside the Get Help app and are reached by
/// deep link. A family that cannot be started falls through to the other one, ending at the Settings
/// page that hosts both, rather than failing with nothing on screen.
/// </summary>
internal static class Troubleshooters
{
    private const string ClassicNetwork = "NetworkDiagnosticsNetworkAdapter";
    private const string ClassicSound = "AudioPlaybackDiagnostic";

    private const string SettingsUri = "ms-settings:troubleshoot";

    /// <summary>
    /// The Get Help app's own troubleshooter deep links, as published in its
    /// Config\NavigationMenuConfig.json (menu "Windows troubleshooters"). Opening the protocol with
    /// no path only lands on the app's home screen; these paths open the troubleshooter itself.
    /// </summary>
    private const string GetHelpNetwork = "ms-contact-support://smc-to-emerald/NetworkAndInternetTroubleshooter";
    private const string GetHelpSound = "ms-contact-support://smc-to-emerald/AudioTroubleshooter";

    /// <summary>Index ids for each target, most specific first.</summary>
    private static readonly string[] NetworkIds = { "NetworkDiagnosticsNetworkAdapter", "NetworkDiagnosticsWeb" };
    private static readonly string[] SoundIds = { "AudioPlaybackDiagnostic", "AudioRecordingDiagnostic" };

    /// <summary>
    /// Index id to Get Help URI, read once from %WINDIR%\diagnostics\index. Windows writes those
    /// files itself - each troubleshooter Settings lists has one - and only the entries typed
    /// "GetHelp" carry a ms-contact-support address. Everything else is an msdt pack.
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<string, string>> GetHelpUris = new(ReadGetHelpUris);

    /// <summary>Opens the troubleshooter for <paramref name="target"/> in the family the user picked.</summary>
    public static void Run(TroubleshooterTarget target, bool classic)
    {
        var pack = target == TroubleshooterTarget.Network ? ClassicNetwork : ClassicSound;
        var ids = target == TroubleshooterTarget.Network ? NetworkIds : SoundIds;

        if (classic)
        {
            if (RunClassic(pack, "classic"))
            {
                return;
            }

            // msdt is gone from this machine: the Get Help troubleshooter is the next real option.
            if (RunGetHelp(target, ids))
            {
                return;
            }
        }
        else
        {
            if (RunGetHelp(target, ids))
            {
                return;
            }

            if (RunClassic(pack, "Get Help did not start; classic"))
            {
                return;
            }
        }

        if (TryStart(SettingsUri, null))
        {
            App.LogInfo($"Troubleshooter: Settings page for {target}");
            return;
        }

        App.LogInfo($"Troubleshooter: nothing could be started for {target}");
    }

    private static bool RunClassic(string pack, string why)
    {
        if (!TryStart("msdt.exe", $"/id {pack}"))
        {
            return false;
        }

        App.LogInfo($"Troubleshooter: {why} {pack}");
        return true;
    }

    private static bool RunGetHelp(TroubleshooterTarget target, IReadOnlyList<string> ids)
    {
        var uri = ResolveGetHelpUri(target, ids);

        if (uri is null || !TryStart(uri, null))
        {
            return false;
        }

        App.LogInfo($"Troubleshooter: Get Help {uri}");
        return true;
    }

    /// <summary>
    /// Where Windows itself says this item's Get Help troubleshooter lives. The Settings list wins
    /// when it has an entry for the item; otherwise the Get Help app's own deep link is used, which
    /// is what "Windows troubleshooters" in that app points at for network and audio.
    /// </summary>
    private static string? ResolveGetHelpUri(TroubleshooterTarget target, IReadOnlyList<string> ids)
    {
        var uris = GetHelpUris.Value;

        foreach (var id in ids)
        {
            if (uris.TryGetValue(id, out var uri))
            {
                return uri;
            }
        }

        return target switch
        {
            TroubleshooterTarget.Network => GetHelpNetwork,
            TroubleshooterTarget.Sound => GetHelpSound,
            _ => null,
        };
    }

    private static IReadOnlyDictionary<string, string> ReadGetHelpUris()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var index = System.IO.Path.Combine(root, "diagnostics", "index");

            foreach (var file in System.IO.Directory.EnumerateFiles(index, "*.xml"))
            {
                try
                {
                    var doc = XDocument.Load(file);
                    var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

                    var id = doc.Descendants(ns + "Id").FirstOrDefault()?.Value.Trim();
                    if (string.IsNullOrEmpty(id))
                    {
                        // The Get Help entries carry no Index block, so their file name is the id.
                        id = System.IO.Path.GetFileNameWithoutExtension(file);
                    }

                    var uri = doc.Descendants(ns + "Package")
                        .FirstOrDefault(p => (string?)p.Attribute("Type") == "GetHelp")
                        ?.Attribute("Path")?.Value.Trim();

                    if (!string.IsNullOrEmpty(uri))
                    {
                        map[id] = uri;
                    }
                }
                catch (Exception ex)
                {
                    App.Log("Troubleshooter index", ex);
                }
            }
        }
        catch (Exception ex)
        {
            // An unreadable index just means no Get Help address is known; the caller falls back.
            App.Log("Troubleshooter index", ex);
        }

        return map;
    }

    private static bool TryStart(string file, string? arguments)
    {
        try
        {
            var info = new ProcessStartInfo(file) { UseShellExecute = true };
            if (arguments is not null)
            {
                info.Arguments = arguments;
            }

            Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            // A missing msdt.exe or an unregistered protocol is a Win32Exception here; there is
            // nothing to show the user about it, because the next family is tried straight away.
            App.Log("Troubleshooter", ex);
            return false;
        }
    }
}
