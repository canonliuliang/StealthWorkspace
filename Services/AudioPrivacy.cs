using System;
using System.Runtime.InteropServices;

namespace StealthWorkspace.Services;

/// <summary>
/// System output mute via WASAPI. The old implementation synthesised a VK_VOLUME_MUTE
/// key press, which *toggled* mute and could therefore unmute the machine (or mute it
/// unexpectedly when the user pressed the boss key twice). This helper instead reads the
/// real mute state and only restores it if we were the one who muted it.
/// </summary>
public static class SystemAudio
{
    private static readonly object Gate = new();
    private static AudioEndpointVolume? _mutedEndpoint;

    public static bool MuteForBossKey()
    {
        lock (Gate)
        {
            if (_mutedEndpoint != null) return true;
            AudioEndpointVolume? endpoint = null;
            try
            {
                endpoint = AudioEndpointVolume.TryOpen();
                if (endpoint == null || !endpoint.TryGetMute(out bool muted)) return false;
                if (muted) return true;
                if (!endpoint.TrySetMute(true)) return false;
                // The user can switch to headphones or a different default output
                // while the cover is up. Restore this precise device on dismissal.
                _mutedEndpoint = endpoint;
                endpoint = null;
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"系统静音不可用: {ex.GetType().Name}");
                return false;
            }
            finally { endpoint?.Dispose(); }
        }
    }

    /// <summary>Restores the previous state only if the boss key muted the system.</summary>
    public static void RestoreAfterBossKey()
    {
        lock (Gate)
        {
            using var endpoint = _mutedEndpoint;
            if (endpoint == null) return;
            _mutedEndpoint = null;
            try
            {
                endpoint.TrySetMute(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"恢复系统静音失败: {ex.GetType().Name}");
            }
        }
    }

    private sealed class AudioEndpointVolume : IDisposable
    {
        private IAudioEndpointVolume? _volume;

        public static AudioEndpointVolume? TryOpen()
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                int hr = enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out IMMDevice? device);
                if (hr != 0 || device == null) return null;

                Guid iid = typeof(IAudioEndpointVolume).GUID;
                hr = device.Activate(ref iid, 0x17 /*CLSCTX_ALL*/, IntPtr.Zero, out object? obj);
                if (hr != 0 || obj is not IAudioEndpointVolume volume) return null;

                return new AudioEndpointVolume { _volume = volume };
            }
            catch (Exception)
            {
                return null;
            }
        }

        public bool TryGetMute(out bool muted)
        {
            muted = false;
            if (_volume == null) return false;
            try
            {
                return _volume.GetMute(out muted) == 0;
            }
            catch
            {
                return false;
            }
        }

        public bool TrySetMute(bool mute)
        {
            if (_volume == null) return false;
            try
            {
                Guid eventContext = Guid.Empty;
                return _volume.SetMute(mute, ref eventContext) == 0;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            _volume = null;
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
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice(string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? interfacePtr);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float min, out float max, out float increment);
    }
}

/// <summary>
/// Coordinates "privacy mute": the app's own players plus the system output endpoint are
/// silenced on boss key, then restored to the *previous* state when leaving the panic
/// state. Also exposes a flag so player pages can stop decoding audio immediately.
/// </summary>
public static class PrivacyMediaGuard
{
    private static readonly object Gate = new();
    private static bool _appAudioMuted;

    /// <summary>Raised on the thread that changed the state; player pages must marshal.</summary>
    public static event Action<bool>? AppAudioMuteChanged;

    public static bool IsAppAudioMuted
    {
        get { lock (Gate) return _appAudioMuted; }
    }

    public static void MuteForBossKey()
    {
        SetAppAudioMuted(true);
        SystemAudio.MuteForBossKey();
    }

    public static void RestoreAfterBossKey()
    {
        SetAppAudioMuted(false);
        SystemAudio.RestoreAfterBossKey();
    }

    public static void SetAppAudioMuted(bool muted)
    {
        lock (Gate)
        {
            if (_appAudioMuted == muted) return;
            _appAudioMuted = muted;
        }
        AppAudioMuteChanged?.Invoke(muted);
    }
}
