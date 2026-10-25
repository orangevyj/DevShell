using System.Runtime.InteropServices;
using System.Text;

namespace DevShell.Interop;

/// <summary>One wireless network as the machine currently sees it.</summary>
internal sealed record WifiNetwork(
    string Ssid,
    string ProfileName,
    int Signal,
    bool Secure,
    bool Connected,
    bool HasProfile,
    bool Connectable,
    int AuthAlgorithm)
{
    /// <summary>The SSID as it should be shown; Windows reports a nameless entry for a hidden network.</summary>
    public string Name => string.IsNullOrWhiteSpace(Ssid) ? "(hidden network)" : Ssid;

    public string Bars => Signal >= 75 ? "\uE874" : Signal >= 50 ? "\uE873" : Signal >= 25 ? "\uE872" : "\uE871";

    public string Lock => Secure ? "\uE72E" : string.Empty;

    public string State => Connected ? "Connected" : HasProfile ? "Saved" : Secure ? "Secured" : "Open";

    public string Detail => Connectable ? $"{State} · {Signal}%" : "Out of range";

    /// <summary>True when joining needs a password typed in rather than a key Windows already has.</summary>
    public bool NeedsPassword => Secure && !HasProfile && AuthAlgorithm != Wlan.Owe;
}

/// <summary>
/// The Wireless LAN API (wlanapi.dll): the only way to list the networks a card can actually see
/// and to join one. Discovery is separate from <see cref="NetworkProbe"/> on purpose — an ethernet
/// machine has no radio at all and must not be asked wireless questions.
///
/// Both list structures are a header followed by a variable-length array, and the array element
/// cannot be described as a marshalled array field without copying the whole block. Entries are
/// therefore read by offset straight out of the memory the API handed back, which is why every
/// offset is checked against the marshaler's own layout before any of them is used
/// (<see cref="LayoutChecked"/>): a wrong stride would quietly read one adapter's name as
/// another's. Buffers come from the WLAN allocator, so they are released with
/// <c>WlanFreeMemory</c> and never with <c>Marshal.FreeHGlobal</c>.
/// </summary>
internal static class Wlan
{
    // DOT11_AUTH_ALGORITHM
    internal const int AuthOpen = 1;
    internal const int AuthSharedKey = 2;
    internal const int AuthWpaPsk = 4;
    internal const int AuthRsnaPsk = 7;   // WPA2-PSK
    internal const int AuthWpa3Sae = 9;
    internal const int Owe = 10;          // Opportunistic Wireless Encryption (enhanced open)

    // WLAN_AVAILABLE_NETWORK dwFlags
    private const uint AvailableNetworkConnected = 0x00000001;
    private const uint AvailableNetworkHasProfile = 0x00000002;

    private const uint ClientVersion = 2;            // Windows Vista and later
    private const uint ErrorSuccess = 0;

    // WLAN_INTERFACE_INFO_LIST: DWORD count, DWORD index, then WLAN_INTERFACE_INFO[]
    private const int InterfaceListHeader = 8;
    private const int InterfaceInfoSize = 532;       // GUID (16) + WCHAR[256] + state (4)
    private const int InterfaceDescriptionOffset = 16;
    private const int InterfaceStateOffset = 528;

    // WLAN_AVAILABLE_NETWORK_LIST: DWORD count, DWORD index, then WLAN_AVAILABLE_NETWORK[]
    private const int NetworkListHeader = 8;
    private const int AvailableNetworkSize = 628;
    private const int SsidOffset = 512;              // DOT11_SSID: ULONG length, UCHAR[32]
    private const int ConnectableOffset = 556;
    private const int SignalQualityOffset = 604;
    private const int SecurityEnabledOffset = 608;
    private const int AuthAlgorithmOffset = 612;
    private const int FlagsOffset = 620;

    /// <summary>A wireless card, as the WLAN service knows it.</summary>
    internal sealed record Radio(Guid Id, string Description, bool Connected);

    /// <summary>True when this machine has a wireless interface at all, i.e. Wi-Fi is worth offering.</summary>
    public static bool IsAvailable()
    {
        try
        {
            return Radios().Count > 0;
        }
        catch (Exception ex)
        {
            App.Log("Wlan.IsAvailable", ex);
            return false;
        }
    }

    public static IReadOnlyList<Radio> Radios()
    {
        using var client = Client.Open();
        if (client is null)
        {
            return Array.Empty<Radio>();
        }

        return client.Radios();
    }

    public static IReadOnlyList<WifiNetwork> Networks()
    {
        using var client = Client.Open();
        if (client is null)
        {
            return Array.Empty<WifiNetwork>();
        }

        var radios = client.Radios();
        return radios.Count == 0 ? Array.Empty<WifiNetwork>() : client.Networks(radios[0].Id);
    }

    /// <summary>Asks the card to look around. Windows reports the results a few seconds later.</summary>
    public static bool Scan()
    {
        using var client = Client.Open();
        if (client is null)
        {
            return false;
        }

        var radios = client.Radios();
        return radios.Count != 0 && client.Scan(radios[0].Id);
    }

    /// <summary>
    /// Stores a profile for <paramref name="network"/> and joins it. An open network is joined with
    /// no key; a saved network reuses the key Windows already has, so <paramref name="password"/>
    /// is only consulted when the profile has to be created here.
    /// </summary>
    public static string? Join(WifiNetwork network, string password)
    {
        using var client = Client.Open();
        if (client is null)
        {
            return "The Wireless LAN service is not running.";
        }

        var radios = client.Radios();
        if (radios.Count == 0)
        {
            return "No wireless adapter.";
        }

        var id = radios[0].Id;
        var profile = string.IsNullOrWhiteSpace(network.ProfileName) ? network.Ssid : network.ProfileName;

        if (!network.HasProfile)
        {
            var (xml, error) = ProfileXml(network.Ssid, network.AuthAlgorithm, network.Secure, password);
            if (xml is null)
            {
                return error;
            }

            var reason = client.SetProfile(id, profile, xml);
            if (reason != ErrorSuccess)
            {
                return $"Windows rejected the network profile (0x{reason:X8}).";
            }
        }

        return client.Connect(id, profile);
    }

    // ---- reading the raw lists ------------------------------------------

    /// <summary>
    /// True once the hand-written offsets below have been checked against the marshaler's layout.
    /// Until then, and forever on a machine where the two disagree, wireless discovery answers
    /// nothing instead of answering nonsense.
    /// </summary>
    internal static bool LayoutChecked { get; } = VerifyLayout();

    /// <summary>Reads a WLAN_INTERFACE_INFO_LIST out of the raw block the API returned.</summary>
    internal static IReadOnlyList<Radio> ParseRadios(IntPtr list)
    {
        var count = Marshal.ReadInt32(list);
        var radios = new List<Radio>(count);

        for (var i = 0; i < count; i++)
        {
            var entry = IntPtr.Add(list, InterfaceListHeader + i * InterfaceInfoSize);

            var guidBytes = new byte[16];
            Marshal.Copy(entry, guidBytes, 0, 16);

            var description = Marshal.PtrToStringUni(IntPtr.Add(entry, InterfaceDescriptionOffset))
                              ?? "Wireless adapter";

            // WLAN_INTERFACE_STATE: 1 is wlan_interface_state_connected.
            radios.Add(new Radio(new Guid(guidBytes), description, Marshal.ReadInt32(entry, InterfaceStateOffset) == 1));
        }

        return radios;
    }

    /// <summary>Reads a WLAN_AVAILABLE_NETWORK_LIST out of the raw block the API returned.</summary>
    internal static IReadOnlyList<WifiNetwork> ParseNetworks(IntPtr list)
    {
        var count = Marshal.ReadInt32(list);
        var networks = new List<WifiNetwork>(count);

        for (var i = 0; i < count; i++)
        {
            var entry = IntPtr.Add(list, NetworkListHeader + i * AvailableNetworkSize);
            var flags = (uint)Marshal.ReadInt32(entry, FlagsOffset);

            var length = Math.Clamp(Marshal.ReadInt32(entry, SsidOffset), 0, 32);
            var ssid = new byte[length];
            if (length > 0)
            {
                Marshal.Copy(IntPtr.Add(entry, SsidOffset + 4), ssid, 0, length);
            }

            networks.Add(new WifiNetwork(
                Encoding.UTF8.GetString(ssid),
                Marshal.PtrToStringUni(entry) ?? string.Empty,     // strProfileName is the first field
                Math.Clamp(Marshal.ReadInt32(entry, SignalQualityOffset), 0, 100),
                Marshal.ReadInt32(entry, SecurityEnabledOffset) != 0,
                (flags & AvailableNetworkConnected) != 0,
                (flags & AvailableNetworkHasProfile) != 0,
                Marshal.ReadInt32(entry, ConnectableOffset) != 0,
                Marshal.ReadInt32(entry, AuthAlgorithmOffset)));
        }

        // Strongest first, the way Windows orders its own list.
        return networks
            .OrderByDescending(n => n.Connected)
            .ThenByDescending(n => n.Signal)
            .ToArray();
    }

    /// <summary>
    /// Checks every offset this file relies on against the same two structures written out for the
    /// marshaler. The two are independent descriptions of one C layout, so agreement between them
    /// is evidence rather than a restatement of the same guess.
    /// </summary>
    private static bool VerifyLayout()
    {
        try
        {
            if (Marshal.SizeOf<AvailableNetworkLayout>() != AvailableNetworkSize
                || Marshal.SizeOf<InterfaceInfoLayout>() != InterfaceInfoSize
                || Marshal.OffsetOf<AvailableNetworkLayout>(nameof(AvailableNetworkLayout.Ssid)) != SsidOffset
                || Marshal.OffsetOf<AvailableNetworkLayout>(nameof(AvailableNetworkLayout.Connectable)) != ConnectableOffset
                || Marshal.OffsetOf<AvailableNetworkLayout>(nameof(AvailableNetworkLayout.SignalQuality)) != SignalQualityOffset
                || Marshal.OffsetOf<AvailableNetworkLayout>(nameof(AvailableNetworkLayout.SecurityEnabled)) != SecurityEnabledOffset
                || Marshal.OffsetOf<AvailableNetworkLayout>(nameof(AvailableNetworkLayout.AuthAlgorithm)) != AuthAlgorithmOffset
                || Marshal.OffsetOf<AvailableNetworkLayout>(nameof(AvailableNetworkLayout.Flags)) != FlagsOffset
                || Marshal.OffsetOf<InterfaceInfoLayout>(nameof(InterfaceInfoLayout.Description)) != InterfaceDescriptionOffset
                || Marshal.OffsetOf<InterfaceInfoLayout>(nameof(InterfaceInfoLayout.State)) != InterfaceStateOffset)
            {
                App.Log("Wlan.Layout", new InvalidOperationException(
                    "the WLAN structures are not laid out the way this code reads them"));
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            App.Log("Wlan.Layout", ex);
            return false;
        }
    }

    // The same two structures again, written for the marshaler instead of for hand-written offsets.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Dot11SsidLayout
    {
        public uint Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Ssid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AvailableNetworkLayout
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        public Dot11SsidLayout Ssid;
        public int BssType;
        public uint NumberOfBssids;
        public bool Connectable;
        public uint NotConnectableReason;
        public uint NumberOfPhyTypes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] PhyTypes;
        public bool MorePhyTypes;
        public uint SignalQuality;
        public bool SecurityEnabled;
        public int AuthAlgorithm;
        public int CipherAlgorithm;
        public uint Flags;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InterfaceInfoLayout
    {
        public Guid Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public int State;
    }

    // ---- profiles --------------------------------------------------------

    /// <summary>
    /// Builds the profile XML Windows needs before it will join anything. The authentication and
    /// encryption pair has to match what the access point advertises, which is why the auth
    /// algorithm read out of the network list is carried all the way here.
    /// </summary>
    private static (string? Xml, string? Error) ProfileXml(string ssid, int auth, bool secure, string password)
    {
        if (string.IsNullOrEmpty(ssid))
        {
            return (null, "That network does not broadcast a name, so DevShell cannot join it.");
        }

        if (!secure)
        {
            return (Profile(ssid, "open", "none", null, null), null);
        }

        if (auth == Owe)
        {
            return (Profile(ssid, "OWE", "AES", null, null), null);
        }

        if (string.IsNullOrEmpty(password))
        {
            return (null, "Enter the network password.");
        }

        return auth switch
        {
            AuthOpen or AuthSharedKey => (Profile(ssid, "open", "WEP", password, "networkKey"), null),
            AuthWpaPsk => (Profile(ssid, "WPAPSK", "TKIP", password, "passPhrase"), null),
            AuthWpa3Sae => (Profile(ssid, "WPA3SAE", "AES", password, "passPhrase"), null),

            // WPA2-PSK, and anything newer this file has no name for.
            _ => (Profile(ssid, "WPA2PSK", "AES", password, "passPhrase"), null),
        };
    }

    private static string Profile(string ssid, string authentication, string encryption, string? key, string? keyType)
    {
        var name = Escape(ssid);
        var sharedKey = key is null
            ? string.Empty
            : $"""

              <sharedKey>
                <keyType>{keyType}</keyType>
                <protected>false</protected>
                <keyMaterial>{Escape(key)}</keyMaterial>
              </sharedKey>
    """;

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>{name}</name>
              <SSIDConfig>
                <SSID>
                  <name>{name}</name>
                </SSID>
              </SSIDConfig>
              <connectionType>ESS</connectionType>
              <connectionMode>auto</connectionMode>
              <MSM>
                <security>
                  <authEncryption>
                    <authentication>{authentication}</authentication>
                    <encryption>{encryption}</encryption>
                    <useOneX>false</useOneX>
                  </authEncryption>{sharedKey}
                </security>
              </MSM>
            </WLANProfile>
            """;
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");

    // ---- the WLAN service ------------------------------------------------

    /// <summary>An open handle to the WLAN service; per-user and cheap to open around one call.</summary>
    private sealed class Client : IDisposable
    {
        private readonly IntPtr _handle;

        private Client(IntPtr handle) => _handle = handle;

        /// <summary>Null when this machine has no Wireless LAN service to talk to.</summary>
        public static Client? Open()
        {
            try
            {
                var code = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var handle);
                return code == ErrorSuccess && handle != IntPtr.Zero ? new Client(handle) : null;
            }
            catch (DllNotFoundException)
            {
                return null;   // a machine without wlanapi.dll has no wireless stack at all
            }
            catch (Exception ex)
            {
                App.Log("Wlan.Open", ex);
                return null;
            }
        }

        public IReadOnlyList<Radio> Radios()
        {
            if (!LayoutChecked)
            {
                return Array.Empty<Radio>();
            }

            var list = IntPtr.Zero;
            try
            {
                return WlanEnumInterfaces(_handle, IntPtr.Zero, out list) == ErrorSuccess
                    ? ParseRadios(list)
                    : Array.Empty<Radio>();
            }
            catch (Exception ex)
            {
                App.Log("Wlan.Radios", ex);
                return Array.Empty<Radio>();
            }
            finally
            {
                Free(ref list);
            }
        }

        public IReadOnlyList<WifiNetwork> Networks(Guid id)
        {
            if (!LayoutChecked)
            {
                return Array.Empty<WifiNetwork>();
            }

            var list = IntPtr.Zero;
            try
            {
                return WlanGetAvailableNetworkList(_handle, ref id, 0, IntPtr.Zero, out list) == ErrorSuccess
                    ? ParseNetworks(list)
                    : Array.Empty<WifiNetwork>();
            }
            catch (Exception ex)
            {
                App.Log("Wlan.Networks", ex);
                return Array.Empty<WifiNetwork>();
            }
            finally
            {
                Free(ref list);
            }
        }

        public bool Scan(Guid id) => WlanScan(_handle, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == ErrorSuccess;

        /// <summary>Stores a profile and reports why Windows refused it, or 0 when it took it.</summary>
        public uint SetProfile(Guid id, string name, string xml)
            => WlanSetProfile(_handle, ref id, 0, xml, null, true, IntPtr.Zero, out var reason) == ErrorSuccess
                ? ErrorSuccess
                : reason;

        /// <summary>Joins a stored profile by name; the SSID travels inside the profile itself.</summary>
        public string? Connect(Guid id, string profile)
        {
            var parameters = new WlanConnectionParameters
            {
                ConnectionMode = 0,          // wlan_connection_mode_profile
                Profile = profile,
                Ssid = IntPtr.Zero,
                BssidList = IntPtr.Zero,
                BssType = 1,                 // dot11_BSS_type_infrastructure
                Flags = 0,
            };

            var code = WlanConnect(_handle, ref id, ref parameters, IntPtr.Zero);
            return code == ErrorSuccess ? null : $"Windows could not join \u201c{profile}\u201d (0x{code:X8}).";
        }

        public void Dispose() => WlanCloseHandle(_handle, IntPtr.Zero);

        private static void Free(ref IntPtr list)
        {
            if (list != IntPtr.Zero)
            {
                WlanFreeMemory(list);
                list = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// WLAN_CONNECTION_PARAMETERS. The profile is passed by name, so the SSID and BSSID list stay
    /// null; the layout (and therefore the padding after the leading enum) is the marshaler's job.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WlanConnectionParameters
    {
        public int ConnectionMode;                                 // WLAN_CONNECTION_MODE
        [MarshalAs(UnmanagedType.LPWStr)] public string Profile;   // LPCWSTR
        public IntPtr Ssid;
        public IntPtr BssidList;
        public int BssType;                                        // DOT11_BSS_TYPE
        public uint Flags;
    }

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanScan(IntPtr handle, ref Guid interfaceId, IntPtr ssid, IntPtr ieData, IntPtr reserved);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanGetAvailableNetworkList(IntPtr handle, ref Guid interfaceId, uint flags, IntPtr reserved, out IntPtr networkList);

    [DllImport("wlanapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint WlanSetProfile(IntPtr handle, ref Guid interfaceId, uint flags, string profileXml, string? allUserProfileSecurity, bool overwrite, IntPtr reserved, out uint reason);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanConnect(IntPtr handle, ref Guid interfaceId, ref WlanConnectionParameters parameters, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}
