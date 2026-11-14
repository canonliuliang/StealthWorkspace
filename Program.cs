using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using StealthWorkspace.Services;

namespace StealthWorkspace;

public static class Program
{
    /// <summary>Strong reference to the Application object; required so it is not collected.</summary>
    public static App? CurrentApp { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        // Developer diagnostic: decrypt the DPAPI protected app log into a plain file.
        if (args.Any(a => string.Equals(a, "--dump-log", StringComparison.OrdinalIgnoreCase)))
        {
            string? root = SelfTest.GetDataRootArgument(args);
            if (!string.IsNullOrWhiteSpace(root)) AppPaths.SetDataRootOverride(root);
            string? target = SelfTest.GetArgumentValue(args, "--dump-log");
            if (string.IsNullOrWhiteSpace(target))
            {
                target = Path.Combine(Environment.CurrentDirectory, "app-log-dump.txt");
            }
            try
            {
                File.WriteAllText(target, AppLog.ReadAll());
            }
            catch
            {
                // ignored
            }
            return;
        }

        // Headless diagnostics entry point: verifies crypto, vault and vault streaming
        // without starting XAML. Used by developers to validate a build.
        if (SelfTest.IsSelfTestRequested(args))
        {
            Environment.ExitCode = SelfTest.Run(args);
            return;
        }

        if (SelfTest.IsSmokeTestRequested(args) || SelfTest.IsUiSmokeRequested(args))
        {
            bool uiSmoke = SelfTest.IsUiSmokeRequested(args);
            string? dataRoot = SelfTest.GetDataRootArgument(args);
            // Every diagnostic process uses its own data root, including a bare
            // --smoke-test. Its separate instance mutex must never expose live data.
            if (string.IsNullOrWhiteSpace(dataRoot))
                dataRoot = Path.Combine(Path.GetTempPath(), "StealthWorkspace.Diagnostics", Guid.NewGuid().ToString("N"));
            string? testLegacy = uiSmoke ? SelfTest.GetArgumentValue(args, "--test-legacy-root") : null;
            AppPaths.SetDataRootOverride(testLegacy == null ? dataRoot :
                AppPaths.ResolveDataRoot(testLegacy, dataRoot, dataRoot + "-fallback"));

            SmokeTest.Enable(args);
        }

        bool ownsInstance = false;
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            if (!(ownsInstance = SingleInstance.TryAcquire()))
            {
                SingleInstance.SignalExistingInstance();
                return;
            }
            // Resolve the persistent vault only after claiming the single-instance mutex.
            AppLog.Initialize();

            Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = p;
                CurrentApp = new App();
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("应用启动失败", ex);
            ShowFatalStartupError();
        }
        finally
        {
            SingleInstance.Shutdown();
            if (ownsInstance) AppLog.FlushAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            if (ownsInstance && SmokeTest.Enabled)
            {
                SmokeTest.Write();
            }
        }
    }

    internal static void ShowFatalStartupError()
    {
        try
        {
            NativeMethods.MessageBoxW(
                IntPtr.Zero,
                "工作台启动失败。诊断信息已加密记录到本地日志。\n\n如问题持续，请尝试重新启动系统后再打开。",
                "StealthWorkspace",
                0x00000010 /* MB_ICONERROR */);
        }
        catch
        {
            // ignored
        }
    }
}
