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
    private static bool _mutedByUs;

    public static bool MuteForBossKey()
    {
        lock (Gate)
        {
            try
            {
                bool muted;
                if (!TryGetMuteCore(out muted)) return false;
                if (muted)
                {
                    // Already muted by the user or another app: never touch it.
                    _mutedByUs = false;
                    return true;
                }

                bool ok = TrySetMuteCore(true);
                _mutedByUs = ok;
                return ok;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"系统静音不可用: {ex.GetType().Name}");
                return false;
            }
        }
    }

    /// <summary>Restores the previous state only if the boss key muted the system.</summary>
    public static void RestoreAfterBossKey()
    {
        lock (Gate)
        {
            if (!_mutedByUs) return;
            _mutedByUs = false;
            try
            {
                TrySetMuteCore(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"恢复系统静音失败: {ex.GetType().Name}");
            }
        }
    }

    private static bool TryGetMuteCore(out bool muted)
    {
        muted = false;
        using var endpoint = AudioEndpointVolume.TryOpen();
        if (endpoint == null)
        {
            return false;
        }
        return endpoint.TryGetMute(out muted);
    }

    private static bool TrySetMuteCore(bool mute)
    {
        using var endpoint = AudioEndpointVolume.TryOpen();
        return endpoint != null && endpoint.TrySetMute(mute);
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
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        int GetDevice(string id, out IMMDevice? device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? interfacePtr);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float level, ref Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        int GetMasterVolumeLevel(out float level);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float level, ref Guid eventContext);
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        int GetChannelVolumeLevel(uint channel, out float level);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        int GetVolumeStepInfo(out uint step, out uint stepCount);
        int VolumeStepUp(ref Guid eventContext);
        int VolumeStepDown(ref Guid eventContext);
        int QueryHardwareSupport(out uint mask);
        int GetVolumeRange(out float min, out float max, out float increment);
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
