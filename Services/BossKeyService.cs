using System;
using System.Runtime.InteropServices;

namespace StealthWorkspace.Services;

/// <summary>
/// Owns the Win32 pieces attached to the main window:
///  - display affinity (anti screenshot / capture exclusion),
///  - taskbar peek exclusion,
///  - the global boss-key hotkey,
///  - a window subclass used to observe minimise/hide/show reliably
///    (the previous implementation listened for presenter changes, which are not raised
///    when a normal window is minimised, so "lock on minimise" never fired).
/// </summary>
public class BossKeyService
{
    private static BossKeyService? _instance;
    public static BossKeyService Instance => _instance ??= new BossKeyService();

    private const nuint SubclassId = 101;
    private const byte VkBossKey = 0x42; // 'B'

    private IntPtr _windowHandle = IntPtr.Zero;
    private NativeMethods.SubclassProc? _subclassProc;
    private bool _isSubclassed;

    public bool IsDisplayAffinityProtected { get; private set; }
    public bool IsBossKeyEnabled { get; private set; }
    public bool IsPeekExcluded { get; private set; }
    public bool IsWindowMinimized { get; private set; }
    public bool SessionNotificationsRegistered { get; private set; }
    public string LastBossKeyError { get; private set; } = string.Empty;

    public event Action? BossKeyPressed;
    public event Action? WindowMinimized;
    public event Action? SystemSessionLeaving;
    public event Action? SystemResumed;
    public event Action? WindowShown;
    public event Action<bool>? WindowVisibilityChanged;

    private BossKeyService()
    {
    }

    public void Initialize(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        if (_windowHandle == IntPtr.Zero)
        {
            LastBossKeyError = "窗口句柄无效。";
            AppLog.Error("初始化窗口防护失败：窗口句柄为空", null);
            return;
        }

        var settings = SettingsService.Instance.Data;
        SetDisplayAffinityProtection(settings.DisplayAffinityProtected);
        SetTaskbarPeekProtection(settings.TaskbarPeekExcluded);
        InstallSubclass();
        RegisterSessionNotifications();
        EnableBossKey(settings.BossKeyEnabled);
    }

    #region Subclassing

    internal void RegisterSessionNotifications()
    {
        if (!_isSubclassed || SessionNotificationsRegistered) return;
        SessionNotificationsRegistered = NativeMethods.WTSRegisterSessionNotification(_windowHandle, 0);
        if (!SessionNotificationsRegistered) AppLog.Warn("未能订阅当前 Windows 会话通知");
    }

    private void InstallSubclass()
    {
        if (_isSubclassed || _windowHandle == IntPtr.Zero) return;

        _subclassProc = WndProcSubclass;
        _isSubclassed = NativeMethods.SetWindowSubclass(_windowHandle, _subclassProc, SubclassId, 0);
        if (!_isSubclassed)
        {
            AppLog.Error("安装窗口子类化失败", null);
        }
    }

    private IntPtr WndProcSubclass(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        try
        {
            switch (uMsg)
            {
                case NativeMethods.WM_WTSSESSION_CHANGE when wParam.ToInt64() is NativeMethods.WTS_SESSION_LOCK
                    or NativeMethods.WTS_CONSOLE_DISCONNECT or NativeMethods.WTS_REMOTE_DISCONNECT:
                case NativeMethods.WM_POWERBROADCAST when wParam.ToInt64() == NativeMethods.PBT_APMSUSPEND:
                    SystemSessionLeaving?.Invoke();
                    break;
                case NativeMethods.WM_POWERBROADCAST when wParam.ToInt64() is 7 or 18: // resume from suspend, automatic resume
                    SystemResumed?.Invoke();
                    break;
                case NativeMethods.WM_HOTKEY:
                    if (wParam.ToInt32() == NativeMethods.HOTKEY_BOSS_KEY_ID)
                    {
                        TriggerBossKey();
                        return IntPtr.Zero;
                    }
                    break;

                case NativeMethods.WM_SIZE:
                    WindowVisibilityChanged?.Invoke(wParam.ToInt32() != NativeMethods.SIZE_MINIMIZED);
                    if (wParam.ToInt32() == NativeMethods.SIZE_MINIMIZED)
                    {
                        IsWindowMinimized = true;
                        WindowMinimized?.Invoke();
                    }
                    else
                    {
                        IsWindowMinimized = NativeMethods.IsIconic(hWnd);
                    }
                    break;

                case NativeMethods.WM_SHOWWINDOW:
                    WindowVisibilityChanged?.Invoke(wParam != IntPtr.Zero && !NativeMethods.IsIconic(hWnd));
                    if (wParam == IntPtr.Zero)
                    {
                        // Hidden. WM_SIZE also arrives when minimising, so no event here.
                    }
                    else
                    {
                        IsWindowMinimized = NativeMethods.IsIconic(hWnd);
                        WindowShown?.Invoke();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("窗口消息处理异常", ex);
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    #endregion

    #region Display protection

    public bool SetDisplayAffinityProtection(bool enable)
    {
        if (_windowHandle == IntPtr.Zero) return false;

        try
        {
            uint affinity = enable ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE;
            bool ok = NativeMethods.SetWindowDisplayAffinity(_windowHandle, affinity);
            if (ok)
            {
                IsDisplayAffinityProtected = enable;
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                AppLog.Warn($"设置防截屏失效 (Win32 {error})");
            }
            return ok;
        }
        catch (Exception ex)
        {
            AppLog.Error("设置防截屏异常", ex);
            return false;
        }
    }

    public bool SetTaskbarPeekProtection(bool enable)
    {
        if (_windowHandle == IntPtr.Zero) return false;

        int value = enable ? 1 : 0;
        bool excluded = SetDwmIntAttribute(NativeMethods.DWMWA_EXCLUDED_FROM_PEEK, value);
        // DWMWA_DISALLOW_PEEK is not supported on every Windows 11 build; treat it as optional.
        SetDwmIntAttribute(NativeMethods.DWMWA_DISALLOW_PEEK, value);

        if (excluded)
        {
            IsPeekExcluded = enable;
        }
        return excluded;
    }

    private bool SetDwmIntAttribute(int attribute, int value)
    {
        try
        {
            int hr = NativeMethods.DwmSetWindowAttribute(_windowHandle, attribute, ref value, sizeof(int));
            return hr == 0;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"设置 DWM 属性 {attribute} 失败: {ex.GetType().Name}");
            return false;
        }
    }

    #endregion

    #region Boss key

    public bool EnableBossKey(bool enable)
    {
        if (_windowHandle == IntPtr.Zero)
        {
            IsBossKeyEnabled = false;
            return false;
        }

        try
        {
            if (enable)
            {
                if (IsBossKeyEnabled) return true;

                bool ok = NativeMethods.RegisterHotKey(
                    _windowHandle,
                    NativeMethods.HOTKEY_BOSS_KEY_ID,
                    NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
                    VkBossKey);

                IsBossKeyEnabled = ok;
                LastBossKeyError = ok
                    ? string.Empty
                    : $"Ctrl+Alt+B 注册失败（可能被其他程序占用，Win32 {Marshal.GetLastWin32Error()}）。";
                if (!ok)
                {
                    AppLog.Warn(LastBossKeyError);
                }
                return ok;
            }

            NativeMethods.UnregisterHotKey(_windowHandle, NativeMethods.HOTKEY_BOSS_KEY_ID);
            IsBossKeyEnabled = false;
            LastBossKeyError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            IsBossKeyEnabled = false;
            LastBossKeyError = "注册全局老板键失败。";
            AppLog.Error("注册老板键异常", ex);
            return false;
        }
    }

    public void TriggerBossKey()
    {
        BossKeyPressed?.Invoke();
    }

    #endregion

    #region Window helpers

    public void MinimizeWindow()
    {
        if (_windowHandle == IntPtr.Zero) return;
        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_MINIMIZE);
        IsWindowMinimized = true;
    }

    public void RestoreWindow()
    {
        if (_windowHandle == IntPtr.Zero) return;
        if (NativeMethods.IsIconic(_windowHandle))
        {
            NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_RESTORE);
        }
        NativeMethods.SetForegroundWindow(_windowHandle);
        IsWindowMinimized = false;
    }

    public void FlashTaskbar()
    {
        if (_windowHandle == IntPtr.Zero) return;
        var info = new NativeMethods.FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.FLASHWINFO>(),
            hwnd = _windowHandle,
            dwFlags = NativeMethods.FLASHW_TRAY,
            uCount = 2,
            dwTimeout = 0
        };
        NativeMethods.FlashWindowEx(ref info);
    }

    public void Uninitialize()
    {
        if (_windowHandle == IntPtr.Zero) return;

        try
        {
            NativeMethods.UnregisterHotKey(_windowHandle, NativeMethods.HOTKEY_BOSS_KEY_ID);
            if (_isSubclassed && _subclassProc != null)
            {
                if (SessionNotificationsRegistered)
                {
                    NativeMethods.WTSUnRegisterSessionNotification(_windowHandle);
                    SessionNotificationsRegistered = false;
                }
                NativeMethods.RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassId);
                _isSubclassed = false;
            }
            _subclassProc = null;
            IsBossKeyEnabled = false;
        }
        catch (Exception ex)
        {
            AppLog.Error("注销窗口防护失败", ex);
        }
        finally
        {
            _windowHandle = IntPtr.Zero;
        }
    }

    #endregion
}
