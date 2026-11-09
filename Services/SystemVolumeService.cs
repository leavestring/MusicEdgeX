using System.Runtime.InteropServices;

namespace MusicEdge.Services;

/// <summary>
/// Controls the default playback device volume through the Core Audio API.
/// </summary>
public sealed class SystemVolumeService : IDisposable
{
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;

    public bool TryGetVolume(out int percent, out bool muted)
    {
        percent = 0;
        muted = false;

        try
        {
            var endpoint = GetEndpoint();
            if (endpoint == null) return false;

            endpoint.GetMasterVolumeLevelScalar(out float level);
            endpoint.GetMute(out bool isMuted);
            percent = (int)Math.Round(level * 100);
            muted = isMuted;
            return true;
        }
        catch
        {
            ResetEndpoint();
            return false;
        }
    }

    public bool TrySetVolume(int percent)
    {
        try
        {
            var endpoint = GetEndpoint();
            if (endpoint == null) return false;

            float level = Math.Clamp(percent, 0, 100) / 100f;
            endpoint.SetMasterVolumeLevelScalar(level, Guid.Empty);
            return true;
        }
        catch
        {
            ResetEndpoint();
            return false;
        }
    }

    public bool TryToggleMute(out bool muted)
    {
        muted = false;

        try
        {
            var endpoint = GetEndpoint();
            if (endpoint == null) return false;

            endpoint.GetMute(out bool current);
            muted = !current;
            endpoint.SetMute(muted, Guid.Empty);
            return true;
        }
        catch
        {
            ResetEndpoint();
            return false;
        }
    }

    private IAudioEndpointVolume? GetEndpoint()
    {
        if (_endpoint != null) return _endpoint;

        _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        _enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out IMMDevice device);
        var iid = typeof(IAudioEndpointVolume).GUID;
        device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out object endpoint);
        _endpoint = (IAudioEndpointVolume)endpoint;
        Marshal.ReleaseComObject(device);
        return _endpoint;
    }

    private void ResetEndpoint()
    {
        if (_endpoint != null)
        {
            try { Marshal.ReleaseComObject(_endpoint); } catch { }
            _endpoint = null;
        }
    }

    public void Dispose()
    {
        ResetEndpoint();
        if (_enumerator != null)
        {
            try { Marshal.ReleaseComObject(_enumerator); } catch { }
            _enumerator = null;
        }
    }

    private enum EDataFlow
    {
        Render = 0
    }

    private enum ERole
    {
        Multimedia = 1
    }

    private enum ClsCtx : uint
    {
        All = 0x17
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
        int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        int GetDevice(string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr callback);
        int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, ClsCtx clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
        int OpenPropertyStore(int access, out IntPtr properties);
        int GetId(out IntPtr id);
        int GetState(out int state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float levelDb, Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, Guid eventContext);
        int GetMasterVolumeLevel(out float levelDb);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float levelDb, Guid eventContext);
        int SetChannelVolumeLevelScalar(uint channel, float level, Guid eventContext);
        int GetChannelVolumeLevel(uint channel, out float levelDb);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid eventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        int GetVolumeStepInfo(out uint step, out uint stepCount);
        int VolumeStepUp(Guid eventContext);
        int VolumeStepDown(Guid eventContext);
        int QueryHardwareSupport(out uint hardwareSupportMask);
        int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }
}
