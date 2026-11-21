using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace StealthWorkspace.Services;

public static partial class BrowserProfileManager
{
    internal const CoreWebView2BrowsingDataKinds SessionHistoryKinds =
        CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DownloadHistory | CoreWebView2BrowsingDataKinds.DiskCache;
    private const CoreWebView2BrowsingDataKinds VisitAndDownloadHistory =
        CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DownloadHistory;
    private static SettingsService.BookmarkSession? _environmentWorkspace;
    private static DispatcherQueue? _ownerDispatcher;
    private static Task? _entryCleanup;
    private static uint _entryBrowserProcess;
    private static readonly ConcurrentDictionary<string, Task<bool>> PendingAccountCleanup = new(StringComparer.OrdinalIgnoreCase);
    internal static bool LastPersonalHistoryCleanupSucceeded { get; private set; } = true;

    private static void RequireCurrentWorkspace(SettingsService.BookmarkSession? session)
    {
        if (session == null || !ReferenceEquals(session, SettingsService.Instance.ActiveBookmarkSession) ||
            !VaultService.Instance.IsUnlocked || VaultService.Instance.IsDecoyMode != session.Temporary)
            throw new OperationCanceledException("浏览空间已锁定或切换。");
    }

    internal static async Task PrepareForNavigationAsync(CoreWebView2 core, CoreWebView2Environment environment, SettingsService.BookmarkSession? session)
    {
        RequireCurrentWorkspace(session);
        if (!ReferenceEquals(environment, _sharedEnvironment) || !ReferenceEquals(_environmentWorkspace, session) || _sessionEnding != null)
            throw new OperationCanceledException("浏览空间正在关闭。");
        core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Balanced;
        if (!session!.Temporary)
        {
            // An environment can outlive its crashed browser process. Each new
            // native writer must finish recovery before any tab navigates.
            uint process = core.BrowserProcessId;
            if (process != _entryBrowserProcess)
            {
                _entryBrowserProcess = process;
                _entryCleanup = null;
            }
            var cleanup = _entryCleanup ??= core.Profile.ClearBrowsingDataAsync(SessionHistoryKinds).AsTask();
            try { await cleanup; }
            catch { if (ReferenceEquals(_entryCleanup, cleanup)) _entryCleanup = null; throw; }
            RequireCurrentWorkspace(session);
            if (_sessionEnding != null || process != _entryBrowserProcess || !ReferenceEquals(_sharedEnvironment, environment) || !ReferenceEquals(session, _environmentWorkspace))
                throw new OperationCanceledException("浏览空间正在关闭。");
            LastPersonalHistoryCleanupSucceeded = true;
        }
    }

    private sealed class HistoryFinalizer
    {
        internal readonly CancellationTokenSource Lifetime = new();
        internal readonly TaskCompletionSource<bool> ControllerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> UserTabsClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Task<bool> Completion;

        internal HistoryFinalizer(CoreWebView2Environment environment) =>
            Completion = ClearClosedProfileHistoryAsync(environment, this);
    }

    private static HistoryFinalizer? _preparedFinalizer;

    // Closing the last WebView lets the native browser process begin exiting.
    // Establish the invisible controller while that browser is still alive.
    // It never navigates and does not clear data until every user tab closed.
    internal static async Task PrepareLastControllerForShutdownAsync(CoreWebView2 core)
    {
        if (_environmentWorkspace is not { Temporary: false } workspace || _sharedEnvironment is not { } environment ||
            Volatile.Read(ref _environmentUsers) != 1 ||
            (_sessionEnding == null && ReferenceEquals(SettingsService.Instance.ActiveBookmarkSession, workspace) && VaultService.Instance.IsUnlocked))
            return;
        try { core.Stop(); core.IsMuted = true; } catch { }
        if (SmokeTest.Enabled) SmokeTest.Report("history cleanup: handing off last live browser tab");
        var finalizer = _preparedFinalizer ??= new HistoryFinalizer(environment);
        try { await finalizer.ControllerReady.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { AppLog.Warn("浏览内核正在交接清理操作，已继续释放网页。"); }
    }

    private static async Task<bool> FinishHistoryWithinDeadlineAsync(CoreWebView2Environment environment, string directory)
    {
        var finalizer = _preparedFinalizer ?? new HistoryFinalizer(environment);
        _preparedFinalizer = null;
        finalizer.UserTabsClosed.TrySetResult(true);
        var work = finalizer.Completion;
        PendingAccountCleanup[directory] = work;
        try
        {
            return await work.WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            finalizer.Lifetime.Cancel();
            AppLog.Warn("浏览历史清理超时。其他空间可以继续；此空间须等待内核结束该操作。");
            return false;
        }
        finally
        {
            // A late completion cannot remove the guard of a subsequent session.
            _ = work.ContinueWith(_ =>
            {
                PendingAccountCleanup.TryRemove(new KeyValuePair<string, Task<bool>>(directory,work));
                finalizer.Lifetime.Dispose();
            },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static async Task WaitForPreviousHistoryOperationAsync(string directory)
    {
        if (!PendingAccountCleanup.TryGetValue(directory, out var prior)) return;
        // A hang in WebView2 must not make a second profile attach, hold the
        // other workspace's lock indefinitely or silently bypass history removal.
        try { await prior.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { throw new IOException("上一次网站历史清理仍在进行，请稍后重试。"); }
    }

    private static Task<bool> ClearClosedProfileHistoryAsync(CoreWebView2Environment environment, HistoryFinalizer finalizer)
    {
        var token = finalizer.Lifetime.Token;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = _ownerDispatcher;
        if (dispatcher == null || !dispatcher.TryEnqueue(async () =>
        {
            if (token.IsCancellationRequested) { finalizer.ControllerReady.TrySetResult(false); completion.TrySetResult(false); return; }
            nint window = 0;
            CoreWebView2Controller? maintenance = null;
            Windows.Foundation.IAsyncInfo? nativeWork = null;
            bool cleared = false;
            using var registration = token.Register(() =>
            {
                try { dispatcher.TryEnqueue(() =>
                {
                    try { nativeWork?.Cancel(); } catch { }
                    try { maintenance?.Close(); } catch { }
                }); } catch { }
            });
            try
            {
                token.ThrowIfCancellationRequested();
                window = CreateWindowExW(0, "STATIC", "", 0x80000000u, 0, 0, 1, 1, 0, 0, 0, 0);
                if (window == 0) throw new IOException("无法创建网站历史清理窗口。");
                var reference = CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)window);
                var creation = environment.CreateCoreWebView2ControllerAsync(reference);
                nativeWork = creation;
                maintenance = await creation;
                token.ThrowIfCancellationRequested();
                maintenance.IsVisible = false;
                finalizer.ControllerReady.TrySetResult(true);
                if (SmokeTest.Enabled) SmokeTest.Report("history cleanup: maintenance controller ready");
                await finalizer.UserTabsClosed.Task;
                token.ThrowIfCancellationRequested();
                var profile = maintenance.CoreWebView2.Profile;
                var clear = profile.ClearBrowsingDataAsync(VisitAndDownloadHistory);
                nativeWork = clear;
                await clear;
                if (SmokeTest.Enabled) SmokeTest.Report("history cleanup: native visit and download records cleared");
                token.ThrowIfCancellationRequested();
                clear = profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
                nativeWork = clear;
                await clear;
                cleared = true;
                if (SmokeTest.Enabled) SmokeTest.Report("history cleanup: native history, downloads and disk cache cleared");
            }
            catch (Exception ex) { AppLog.Warn("网站历史清理未确认，将在下次导航前重试：" + DescribeErrorCode(ex)); }
            finally
            {
                try { maintenance?.Close(); } catch { }
                if (window != 0) DestroyWindow(window);
                finalizer.ControllerReady.TrySetResult(false);
                completion.TrySetResult(cleared);
            }
        }))
        {
            finalizer.ControllerReady.TrySetResult(false);
            completion.TrySetResult(false);
        }
        return completion.Task;
    }

    [DllImport("user32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    internal static async Task ReleaseProfileForReplacementAsync(string directory)
    {
        bool OwnsActive() => IsSameDirectory(directory, _sharedUserDataDirectory);
        await WaitForPreviousHistoryOperationAsync(directory);
        if (!OwnsActive()) return;
        if (Volatile.Read(ref _sessionEnding) is { } ending) await ending.Task;
        if (OwnsActive() && !await ShutdownAsync() && OwnsActive())
            throw new IOException("浏览器仍在使用这个保险箱的网站账户，请关闭网页后重试。");
        await WaitForPreviousHistoryOperationAsync(directory);
    }
}
