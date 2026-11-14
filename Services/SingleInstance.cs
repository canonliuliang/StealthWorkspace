using System;
using System.Threading;
using Microsoft.Windows.AppLifecycle;

namespace StealthWorkspace.Services;

/// <summary>
/// Ensures only one instance of the app owns the vault. Multiple instances writing the
/// same encrypted config concurrently would corrupt it, so the second instance simply
/// brings the existing window back to the foreground and exits.
/// </summary>
public static class SingleInstance
{
    // An explicitly isolated diagnostic vault must not steal activation from the
    // user's running browser, or share its single-instance mutex.
    private static string InstanceSuffix => SmokeTest.Enabled
        ? ".Test." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(AppPaths.DataRoot).ToUpperInvariant())))[..16]
        : ".v1";
    private static string MutexName => @"Local\StealthWorkspace.Instance" + InstanceSuffix;
    private static string ActivationEventName => @"Local\StealthWorkspace.Activate" + InstanceSuffix;
    private static string ActivationKey => "StealthWorkspace" + InstanceSuffix;

    private static Mutex? _mutex;
    private static EventWaitHandle? _activationEvent;
    private static Thread? _listenerThread;
    private static volatile bool _stopping;
    private static AppInstance? _registeredInstance;

    /// <summary>Returns true when this process is the first (owning) instance.</summary>
    public static bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }
            // Keep a second launch's signal while the first window starts.
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            try { _registeredInstance = AppInstance.FindOrRegisterForKey(ActivationKey); }
            catch (Exception ex) { AppLog.Warn("窗口激活注册降级: " + ex.GetType().Name); }
            return true;
        }
        catch (Exception ex)
        {
            // Continuing without ownership could open the same encrypted vault
            // in two writers. Let the startup error path explain the failure.
            throw new InvalidOperationException("无法建立应用单实例保护", ex);
        }
    }

    /// <summary>Signals the already running instance to show itself.</summary>
    public static void SignalExistingInstance()
    {
        try
        {
            // Transfer the shell-launched process's foreground rights to the
            // registered owner before asking that background window to activate.
            try
            {
                var owner = AppInstance.GetInstances().FirstOrDefault(instance => !instance.IsCurrent && instance.Key == ActivationKey);
                if (owner != null) NativeMethods.AllowSetForegroundWindow(owner.ProcessId);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Foreground activation unavailable: " + ex.GetType().Name); }
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (EventWaitHandle.TryOpenExisting(ActivationEventName, out EventWaitHandle? handle))
                {
                    using (handle) handle.Set();
                    return;
                }
                Thread.Sleep(25); // Only the short-lived second launch waits.
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Existing instance activation failed: " + ex.GetType().Name);
        }
    }

    /// <summary>Starts a background listener that invokes <paramref name="onActivate"/> on demand.</summary>
    public static void StartActivationListener(Action onActivate)
    {
        try
        {
            _activationEvent ??= new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            _listenerThread = new Thread(() =>
            {
                while (!_stopping)
                {
                    try
                    {
                        if (!_activationEvent.WaitOne(TimeSpan.FromSeconds(1))) continue;
                        if (_stopping) break;
                        onActivate();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("激活已有实例回调异常", ex);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "StealthWorkspace.SingleInstanceListener"
            };
            _listenerThread.Start();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"单实例监听启动失败: {ex.GetType().Name}");
        }
    }

    public static void Shutdown()
    {
        _stopping = true;
        try { if (_registeredInstance?.IsCurrent == true) _registeredInstance.UnregisterKey(); } catch { }
        _registeredInstance = null;
        try { _activationEvent?.Set(); } catch { }
        try { _activationEvent?.Dispose(); } catch { }
        _activationEvent = null;

        try
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch
        {
            // ignored
        }
        _mutex = null;
    }
}
