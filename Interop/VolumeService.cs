using System.Runtime.InteropServices;

namespace DevShell.Interop;

/// <summary>
/// System volume through the Core Audio API (the same endpoint the real taskbar uses),
/// so the bar works even when the per-app mixer has other things muted.
/// </summary>
internal sealed class VolumeService
{
    private const int EDataFlowRender = 0;
    private const int ERoleConsole = 0;
    private const int ClsCtxAll = 23;

    private static readonly Guid IAudioEndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _volume;

    public bool IsAvailable { get; private set; }

    public float Level { get; private set; }

    public bool Muted { get; private set; }

    public bool Refresh()
    {
        try
        {
            if (_volume is null && !Acquire())
            {
                IsAvailable = false;
                return false;
            }

            if (_volume!.GetMasterVolumeLevelScalar(out var level) != 0)
            {
                _volume = null;
                IsAvailable = false;
                return false;
            }

            _volume.GetMute(out var muted);

            Level = Math.Clamp(level, 0f, 1f);
            Muted = muted;
            IsAvailable = true;
            return true;
        }
        catch (Exception ex)
        {
            App.Log("VolumeService.Refresh", ex);
            _volume = null;
            IsAvailable = false;
            return false;
        }
    }

    public bool SetLevel(float level)
    {
        level = Math.Clamp(level, 0f, 1f);

        if (_volume is null && !Acquire())
        {
            return false;
        }

        try
        {
            var context = Guid.Empty;
            if (_volume!.SetMasterVolumeLevelScalar(level, ref context) != 0)
            {
                return false;
            }

            Level = level;
            if (level > 0f && Muted)
            {
                // Dragging the slider up on a muted device should unmute, as on the real taskbar.
                SetMuted(false);
            }

            return true;
        }
        catch (Exception ex)
        {
            App.Log("VolumeService.SetLevel", ex);
            _volume = null;
            return false;
        }
    }

    public bool ToggleMute() => SetMuted(!Muted);

    private bool SetMuted(bool muted)
    {
        if (_volume is null && !Acquire())
        {
            return false;
        }

        try
        {
            var context = Guid.Empty;
            if (_volume!.SetMute(muted, ref context) != 0)
            {
                return false;
            }

            Muted = muted;
            return true;
        }
        catch (Exception ex)
        {
            App.Log("VolumeService.SetMuted", ex);
            _volume = null;
            return false;
        }
    }

    private bool Acquire()
    {
        try
        {
            _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

            if (_enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleConsole, out var device) != 0 || device is null)
            {
                return false;
            }

            var iid = IAudioEndpointVolumeId;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var endpoint) != 0 || endpoint is null)
            {
                return false;
            }

            _volume = (IAudioEndpointVolume)endpoint;
            return true;
        }
        catch (Exception ex)
        {
            App.Log("VolumeService.Acquire", ex);
            _enumerator = null;
            _volume = null;
            return false;
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);

        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);

        int RegisterEndpointNotificationCallback(IntPtr client);

        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

        int OpenPropertyStore(int access, out IntPtr properties);

        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        int GetState(out int state);
    }

    /// <summary>
    /// Only the first thirteen vtable entries are declared — everything up to GetMute,
    /// which is all this app calls. The order must match the SDK exactly.
    /// </summary>
    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);

        int UnregisterControlChangeNotify(IntPtr notify);

        int GetChannelCount(out int channelCount);

        int SetMasterVolumeLevel(float level, ref Guid eventContext);

        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        int GetMasterVolumeLevel(out float level);

        int GetMasterVolumeLevelScalar(out float level);

        int SetChannelVolumeLevel(int channel, float level, ref Guid eventContext);

        int SetChannelVolumeLevelScalar(int channel, float level, ref Guid eventContext);

        int GetChannelVolumeLevel(int channel, out float level);

        int GetChannelVolumeLevelScalar(int channel, out float level);

        int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid eventContext);

        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
