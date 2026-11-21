using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using StealthWorkspace.Services;

namespace StealthWorkspace;

public partial class App : Application
{
    public static Window? MainWindowInstance { get; set; }

    public App()
    {
        AppLog.Initialize();
        this.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            SmokeTest.Report("OnLaunched: 创建主窗口");
            MainWindowInstance = new MainWindow();
            // Set before the last window closes; changing it inside Closed is too late.
            // MainWindow exits explicitly after asynchronous privacy cleanup completes.
            DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
            if (SmokeTest.ShieldVisualAudit)
            {
                MainWindowInstance.AppWindow.Show(activateWindow: false);
                SmokeTest.Report("shield fixture window shown without activating");
            }
            else
            {
                MainWindowInstance.Activate();
                SmokeTest.Report("MainWindow 已激活");
            }

            SingleInstance.StartActivationListener(() =>
            {
                if (MainWindowInstance is MainWindow window)
                {
                    window.DispatcherQueue.TryEnqueue(window.BringToForeground);
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("创建主窗口失败", ex);
            SmokeTest.Report("launch-failed: " + ex.GetType().Name + " | " + ex.Message);
            Program.ShowFatalStartupError();
            try { Exit(); } catch { }
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Keep the app alive but never silently swallow: the encrypted diagnostic log is
        // the place where these are recorded now.
        AppLog.Error("未处理的 UI 异常", e.Exception);
        SmokeTest.Report("unhandled-ui-exception: " + e.Exception?.GetType().Name +
            (e.Exception is TypeLoadException missingType ? " " + missingType.TypeName : string.Empty));
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        AppLog.Error("未处理的域异常", e.ExceptionObject as Exception);
        AppLog.FlushAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("未观察的任务异常", e.Exception);
        e.SetObserved();
    }
}
