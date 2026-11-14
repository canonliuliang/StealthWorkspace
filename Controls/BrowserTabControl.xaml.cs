using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using StealthWorkspace.Models;
using StealthWorkspace.Services;
using Windows.System;
// Windows.System also contains a DispatcherQueue type; alias ours explicitly to avoid CS0104.
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl : UserControl
{
    private const int InitializationWaitTimeoutMs = 2500;
    private static readonly TimeSpan BackgroundIdleDelay = TimeSpan.FromMinutes(1);

    private static readonly List<WeakReference<BrowserTabControl>> LiveTabs = new();
    private static readonly object LiveTabsGate = new();
    private static DispatcherQueue? _uiDispatcherQueue;
    private static int _initializedTabCount;
    private static bool _hostWindowVisible = true;

    /// <summary>Number of tabs whose CoreWebView2 has been initialized in this process.
    /// Used by the UI smoke test to prove the browser kernel actually started.</summary>
    public static int InitializedTabCount => Volatile.Read(ref _initializedTabCount);
    internal static int LiveTabCount => LiveTabs.Count(w => w.TryGetTarget(out var tab) && !tab._isDisposed);

    /// <summary>Last browser kernel initialization error (type/code only, no paths/URLs).</summary>
    public static string LastInitializationError { get; private set; } = string.Empty;

    private readonly ObservableCollection<CookieDisplayItem> _allCookies = new();
    private readonly ObservableCollection<CookieDisplayItem> _filteredCookies = new();
    private readonly ObservableCollection<HistorySessionItem> _sessionHistory = BrowserSessionHistory.Items;
    private readonly object _disposeSync = new();

    private Task? _disposeTask;
    private Task? _initializationTask;
    private TaskCompletionSource<bool>? _initializationCompletion;
    private CoreWebView2? _hookedCore;
    private WebView2? _browserControl;
    private readonly HashSet<CoreWebView2DownloadOperation> _activeDownloads = new();
    private readonly Dictionary<CoreWebView2DownloadOperation, BrowserDownloadPath> _downloadPaths = new();
    private readonly Dictionary<CoreWebView2DownloadOperation, BrowserDownloadItem> _downloadRecords = new();
    private readonly Dictionary<CoreWebView2DownloadOperation, (Windows.Foundation.TypedEventHandler<CoreWebView2DownloadOperation, object> State,
        Windows.Foundation.TypedEventHandler<CoreWebView2DownloadOperation, object> Progress)> _downloadHandlers = new();
    private CoreWebView2DownloadOperation? _displayedDownload;
    private long _downloadProgressUpdatedAt;
    private bool HasRunningDownloads => _activeDownloads.Any(d => d.State == CoreWebView2DownloadState.InProgress);
    internal bool HasPendingDownloads => _activeDownloads.Count != 0;
    internal event Action? DownloadsActivityChanged;
    private bool _retainedForDownloads;
    private Flyout? _downloadsFlyout;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _backgroundTimer;
    private bool _isSelected;
    private bool _suspending;
    private bool _backgroundMemoryReduced;
    private int _suspendRetries;
    private bool _isPlayingAudio;
    private BitmapImage? _favicon;
    private int _faviconVersion;
    private bool _isBrowserFullScreen;
    private TimeSpan _backgroundIdleDelay = BackgroundIdleDelay;
    private bool _isInitialized;
    private bool _needsRecreate;
    private bool _isInitializing;
    private string? _pendingNavigationUrl;
    private string? _lastRequestedUrl;
    private string? _downloadNavigationUrl;
    private string? _addressAtNavigationStart;
    private ulong? _activeNavigationId;
    private bool _documentReady;
    private bool _historyRecorded;
    private bool _focusPageAfterNavigation;
    private bool _navigationFailed;
    private bool _focusAddressOnLoad;
    private bool _settingsSubscribed;
    private bool _environmentAcquired;
    private bool _isUpdatingToggles;
    private string _hubStatus = "浏览数据保存在当前临时会话中。";
    private int _blockedCounter;
    private volatile bool _isDisposed;

    public event Action<string>? TitleChanged;
    public event Action<bool>? AudioPlayingChanged;
    public event Action<string, bool>? NewTabRequested;
    internal Func<bool, Task<BrowserTabControl?>>? PopupRequested;
    internal event Action? CloseRequested;
    public event Action<bool>? FullScreenModeChanged;
    public event Action? NavigationVisualChanged;

    public IconSource TabIcon => _activeNavigationId != null && !_documentReady
        ? new FontIconSource { Glyph = "\uE895" }
        : _isPlayingAudio
            ? new FontIconSource { Glyph = _hookedCore?.IsMuted == true ? "\uE74F" : "\uE767" }
            : _favicon != null ? new ImageIconSource { ImageSource = _favicon }
            : new FontIconSource { Glyph = "\uE774" };

    public string CurrentUrl
    {
        get
        {
            try
            {
                return GetCurrentDocumentUrl();
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    internal bool HasBrowserControl => _browserControl != null;
    internal bool IsPageFullScreen => _isBrowserFullScreen;
    internal bool HasPrivacyUi => PrivacyContent != null;
    internal bool IsSmokeSuspended => _hookedCore?.IsSuspended == true;
    internal bool IsSmokeBrowserAwake => _hookedCore is { IsSuspended: false };
    internal bool IsSmokeNavigationComplete => _activeNavigationId == null && _isInitialized && !_isInitializing;

    public string CurrentTitle { get; private set; } = "新标签页";

    public BrowserTabControl()
    {
        InitializeComponent();

        _uiDispatcherQueue ??= this.DispatcherQueue;
        RegisterLiveTab(this);

        Loaded += BrowserTabControl_Loaded;
        Unloaded += BrowserTabControl_Unloaded;
        PreviewKeyDown += BrowserTabControl_PreviewKeyDown;

        UpdateEmptyNotices();
    }

    #region Lifetime & Disposal

    private void BrowserTabControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isDisposed) return;

        SubscribeSettings();
        AttachFaviconScale();
        LoadPermissionSettings();
        UpdateEmptyNotices();
        UpdateBookmarkStarState();
        RefreshStartBookmarks();
        if (_focusAddressOnLoad)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!_focusAddressOnLoad || !IsLoaded || _isDisposed || !_isSelected) return;
                _focusAddressOnLoad = false;
                FocusAddressBar();
            });
        }
    }

    internal void FocusAddressWhenLoaded() => _focusAddressOnLoad = true;

    internal void SetTabSelected(bool selected)
    {
        if (_isDisposed || _isSelected == selected) return;
        _isSelected = selected;
        if (!selected) ExitPageFullScreen();
        if (!selected) HideZoomPanel();
        UpdateBrowserVisibility();
        if (IsForegroundTab)
        {
            _backgroundTimer?.Stop();
            ResumeBrowser();
            UiMotion.Enter(TopNavBar, 0, 0);
        }
        else ScheduleBackgroundSleep();
    }

    private bool IsForegroundTab => _isSelected && _hostWindowVisible;

    internal static void SetHostWindowVisible(bool visible)
    {
        if (_hostWindowVisible == visible) return;
        _hostWindowVisible = visible;
        foreach (var tab in SnapshotLiveTabs())
        {
            tab.UpdateBrowserVisibility();
            if (tab.IsForegroundTab) { tab._backgroundTimer?.Stop(); tab.ResumeBrowser(); }
            else tab.ScheduleBackgroundSleep();
        }
    }

    private bool CanSleepInBackground => !_isDisposed && !IsForegroundTab && !_isInitializing &&
        _isInitialized && !_suspending && !_backgroundMemoryReduced && !_isPlayingAudio && !_isBrowserFullScreen &&
        !HasRunningDownloads && (_activeNavigationId == null || _documentReady);

    private void UpdateBrowserVisibility()
    {
        // TabView unloading and window minimisation do not always propagate native
        // WebView visibility. TrySuspend requires an actually hidden controller.
        if (_browserControl is { } browser)
            browser.Visibility = IsForegroundTab ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScheduleBackgroundSleep(TimeSpan? retryDelay = null)
    {
        _backgroundTimer?.Stop();
        if (!CanSleepInBackground) return;

        if (_backgroundTimer == null)
        {
            _backgroundTimer = DispatcherQueue.CreateTimer();
            _backgroundTimer.IsRepeating = false;
            _backgroundTimer.Tick += BackgroundTimer_Tick;
        }
        if (retryDelay == null) _suspendRetries = 0;
        _backgroundTimer.Interval = retryDelay ?? (!_hostWindowVisible && _backgroundIdleDelay > TimeSpan.FromSeconds(15)
            ? TimeSpan.FromSeconds(15) : _backgroundIdleDelay);
        _backgroundTimer.Start();
    }

    private async void BackgroundTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!CanSleepInBackground || _hookedCore is not { } core) return;

        _suspending = true;
        bool suspended = false;
        try
        {
            suspended = core.IsSuspended || await core.TrySuspendAsync();
            if (SmokeTest.Enabled) SmokeTest.Report($"browser sleep attempt: accepted={suspended}; selected={_isSelected}; hostVisible={_hostWindowVisible}; xamlVisibility={_browserControl?.Visibility}");
            // The selected tab or its activity may have changed during the native call.
            if (!_isDisposed && (IsForegroundTab || _isPlayingAudio || _isBrowserFullScreen ||
                HasRunningDownloads || (_activeNavigationId != null && !_documentReady))) ResumeBrowser();
        }
        catch (Exception ex)
        {
            // The runtime can decline suspension (for example, the view became visible).
            // Browsing continues normally; another period of inactivity can retry.
            if (!_isDisposed) AppLog.Warn($"后台标签休眠未完成（{BrowserProfileManager.DescribeErrorCode(ex)}）");
        }
        finally
        {
            _suspending = false;
            if (!suspended && CanSleepInBackground && _suspendRetries++ < 2)
                ScheduleBackgroundSleep(TimeSpan.FromSeconds(5));
            else if (!suspended && CanSleepInBackground)
            {
                // Some runtimes decline suspension. After bounded retries request
                // lower memory use without discarding the document or form state.
                try
                {
                    core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                    _backgroundMemoryReduced = true;
                }
                catch (Exception ex) { AppLog.Warn("Background memory target: " + ex.GetType().Name); }
            }
        }
    }

    private void ResumeBrowser()
    {
        try
        {
            if (_hookedCore is not { } core) return;
            if (core.IsSuspended) core.Resume();
            if (core.MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Low)
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            _backgroundMemoryReduced = false;
            if (_faviconPending && !_faviconLoading) _faviconTimer?.Start();
        }
        catch (Exception ex)
        {
            if (!_isDisposed) AppLog.Warn($"后台标签恢复未完成（{BrowserProfileManager.DescribeErrorCode(ex)}）");
        }
    }

    private void BrowserTabControl_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachFaviconScale();
        // Do not dispose here: the tab itself is disposed by BrowserPage when it is
        // actually closed. Unloaded can also fire merely because the TabView hides
        // a non-selected tab, which must not tear the WebView2 down.
        UnsubscribeSettings();
    }

    /// <summary>
    /// Disposes every live <see cref="BrowserTabControl"/> (idempotent, never throws).
    /// The actual release always runs on the UI thread; safe to call from any thread.
    /// Used by the app teardown path before BrowserProfileManager.ShutdownAsync().
    /// </summary>
    public static Task ShutdownAllAsync()
    {
        try
        {
            List<BrowserTabControl> tabs = SnapshotLiveTabs();
            DispatcherQueue? dispatcher = _uiDispatcherQueue;
            if (dispatcher == null || dispatcher.HasThreadAccess)
            {
                return DisposeTabsAsync(tabs);
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(() => { _ = DisposeTabsOnUiThreadAsync(tabs, completion); }))
            {
                AppLog.Warn("ShutdownAllAsync: 无法调度到 UI 线程，标签将随进程退出释放");
                completion.TrySetResult(false);
            }

            return completion.Task;
        }
        catch (Exception ex)
        {
            AppLog.Error("ShutdownAllAsync failed", ex);
            return Task.CompletedTask;
        }
    }

    private static async Task DisposeTabsOnUiThreadAsync(
        List<BrowserTabControl> tabs,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await DisposeTabsAsync(tabs);
        }
        catch (Exception ex)
        {
            AppLog.Error("ShutdownAllAsync: 释放标签页失败", ex);
        }
        finally
        {
            completion.TrySetResult(true);
        }
    }

    private static async Task DisposeTabsAsync(List<BrowserTabControl> tabs)
    {
        BrowserDownloads.EndSession();
        BrowserSessionHistory.Items.Clear();
        await Task.WhenAll(tabs.Select(tab => tab.DisposeTabAsync()));
    }

    /// <summary>
    /// Closes this control without clearing the shared session used by other tabs.
    /// </summary>
    public Task DisposeTabAsync()
    {
        Task result;
        TaskCompletionSource<bool> completion;

        lock (_disposeSync)
        {
            if (_disposeTask != null)
            {
                return _disposeTask;
            }

            _isDisposed = true;

            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            result = completion.Task;
            _disposeTask = result;
        }

        _ = RunDisposeAsync(completion);
        return result;
    }

    private async Task RunDisposeAsync(TaskCompletionSource<bool> completion)
    {
        bool completedHere = false;
        try
        {
            DispatcherQueue? dispatcher = this.DispatcherQueue;
            if (dispatcher == null || dispatcher.HasThreadAccess)
            {
                completedHere = true;
                await DisposeCoreAsync();
            }
            else if (!dispatcher.TryEnqueue(() => { _ = DisposeCoreOnUiThreadAsync(completion); }))
            {
                completedHere = true;
                AppLog.Warn("DisposeTabAsync: 无法调度到 UI 线程，跳过 WebView2 释放");

                // We cannot touch the WebView2 from here, but the environment lease
                // must still be returned so BrowserProfileManager.ShutdownAsync can finish.
                UnregisterLiveTab(this);
                if (_environmentAcquired)
                {
                    _environmentAcquired = false;
                    BrowserProfileManager.ReleaseEnvironmentUse();
                }
            }
        }
        catch (Exception ex)
        {
            // No UI callback was successfully queued if dispatch itself threw.
            // Complete this task so ending a session cannot wait indefinitely.
            completedHere = true;
            AppLog.Error("DisposeTabAsync failed", ex);
        }
        finally
        {
            if (completedHere)
            {
                completion.TrySetResult(true);
            }
        }
    }

    private async Task DisposeCoreOnUiThreadAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            await DisposeCoreAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("DisposeTabAsync failed", ex);
        }
        finally
        {
            completion.TrySetResult(true);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            if (_backgroundTimer != null)
            {
                _backgroundTimer.Stop();
                _backgroundTimer.Tick -= BackgroundTimer_Tick;
                _backgroundTimer = null;
            }
            // Never touch the WebView2 in the middle of its initialization handshake.
            await WaitForInitializationAsync();

            foreach (var download in _activeDownloads.ToArray())
            {
                try { download.Cancel(); } catch { }
                DetachDownloadStateHandler(download);
            }
            _downloadsFlyout?.Hide();
            _downloadsFlyout = null;
            _displayedDownload = null;
            DetachBookmarkUi();
            foreach (var reservation in _downloadPaths.Values) reservation.Dispose();
            _downloadPaths.Clear();
            DetachCoreWebView2Handlers();
            DetachFaviconScale();
            UnsubscribeSettings();
            UnregisterLiveTab(this);

            try
            {
                // Only close a WebView2 that actually finished initialization. Calling
                // Close() on a control whose environment creation failed can fail-fast
                // inside the native WebView2 runtime instead of throwing manageably.
                if (_browserControl is { } browser)
                {
                    browser.NavigationStarting -= BrowserControl_NavigationStarting;
                    browser.NavigationCompleted -= BrowserControl_NavigationCompleted;
                    browser.RemoveHandler(PointerPressedEvent, new PointerEventHandler(BrowserPointerPressed));
                    if (_isInitialized || browser.CoreWebView2 != null) browser.Close();
                    BrowserHost.Children.Remove(browser);
                    _browserControl = null;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("DisposeTabAsync: 关闭 WebView2 失败", ex);
            }

            _isInitialized = false;
            BrowserProgressBar.Visibility = Visibility.Collapsed;
            _allCookies.Clear();
            _filteredCookies.Clear();
            UrlTextBox.ItemsSource = null;
            StartSearchBox.ItemsSource = null;
            UpdateEmptyNotices();
            // The page's delegates capture the TabViewItem, whose native flyout
            // delegates in turn capture this control. Break that cycle at close.
            TitleChanged = null;
            AudioPlayingChanged = null;
            NewTabRequested = null;
            PopupRequested = null;
            CloseRequested = null;
            DownloadsActivityChanged = null;
            _allowedPopupOrigins.Clear();
            _blockedPopupUrls.Clear();
            FullScreenModeChanged = null;
            NavigationVisualChanged = null;
            _favicon = null;
            Loaded -= BrowserTabControl_Loaded;
            Unloaded -= BrowserTabControl_Unloaded;
            PreviewKeyDown -= BrowserTabControl_PreviewKeyDown;
        }
        catch (Exception ex)
        {
            AppLog.Error("DisposeTabAsync failed", ex);
        }
        finally
        {
            if (_environmentAcquired)
            {
                _environmentAcquired = false;
                BrowserProfileManager.ReleaseEnvironmentUse();
            }
        }
    }

    private async Task WaitForInitializationAsync()
    {
        Task? initialization = _initializationTask;
        if (initialization == null || initialization.IsCompleted) return;

        await Task.WhenAny(initialization, Task.Delay(InitializationWaitTimeoutMs));
    }

    private static void RegisterLiveTab(BrowserTabControl tab)
    {
        lock (LiveTabsGate)
        {
            LiveTabs.Add(new WeakReference<BrowserTabControl>(tab));
        }
    }

    private static void UnregisterLiveTab(BrowserTabControl tab)
    {
        lock (LiveTabsGate)
        {
            LiveTabs.RemoveAll(reference =>
                !reference.TryGetTarget(out BrowserTabControl? target) || ReferenceEquals(target, tab));
        }
    }

    private static List<BrowserTabControl> SnapshotLiveTabs()
    {
        var result = new List<BrowserTabControl>();
        lock (LiveTabsGate)
        {
            LiveTabs.RemoveAll(reference => !reference.TryGetTarget(out _));
            foreach (WeakReference<BrowserTabControl> reference in LiveTabs)
            {
                if (reference.TryGetTarget(out BrowserTabControl? tab))
                {
                    result.Add(tab);
                }
            }
        }

        return result;
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Initializes the WebView2 with the shared volatile environment and navigates to
    /// <paramref name="initialUrl"/> (or the configured private home page). Safe to
    /// call again after a failure or a browser-process crash: it retries instead of
    /// only writing the error into the address bar.
    /// </summary>
    private async Task InitializeAsync(string? initialUrl)
    {
        if (_isDisposed) return;

        CoreWebView2? existingCore = _browserControl?.CoreWebView2;
        if (_isInitialized && existingCore != null)
        {
            if (!string.IsNullOrWhiteSpace(initialUrl))
            {
                Uri? requested = BuildNavigableUri(initialUrl);
                if (requested != null)
                {
                    NavigateTo(requested);
                }
            }

            return;
        }

        if (_isInitializing) return;

        if (_needsRecreate)
        {
            DetachCoreWebView2Handlers();
            foreach (var download in _activeDownloads.ToArray())
            {
                try { download.Cancel(); } catch { }
                DetachDownloadStateHandler(download);
            }
            _backgroundMemoryReduced = false;
            if (_browserControl is { } failedBrowser)
            {
                failedBrowser.NavigationStarting -= BrowserControl_NavigationStarting;
                failedBrowser.NavigationCompleted -= BrowserControl_NavigationCompleted;
                failedBrowser.RemoveHandler(PointerPressedEvent, new PointerEventHandler(BrowserPointerPressed));
                try { failedBrowser.Close(); } catch { }
                BrowserHost.Children.Remove(failedBrowser);
                _browserControl = null;
            }
            _needsRecreate = false;
        }

        _isInitializing = true;
        _isInitialized = false;
        _initializationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _initializationTask = _initializationCompletion.Task;

        try
        {
            BrowserErrorBar.IsOpen = false;
            BrowserProgressBar.Visibility = Visibility.Visible;
            StartMessage.Text = "正在启动浏览器…";
            SearchHomeBtn.IsEnabled = false;

            CoreWebView2Environment environment = await BrowserProfileManager.GetSharedEnvironmentAsync();
            if (_isDisposed) return;

            if (!_environmentAcquired)
            {
                _environmentAcquired = true;
                BrowserProfileManager.AcquireEnvironmentUse();
            }

            WebView2 browser = _browserControl ?? CreateBrowserControl();
            browser.Visibility = Visibility.Visible;
            if (browser.CoreWebView2 == null)
            {
                await browser.EnsureCoreWebView2Async(environment);
            }

            if (_isDisposed)
            {
                // Disposal's bounded wait may have expired while the native
                // controller was starting. Close this late result explicitly.
                try { browser.Close(); } catch { }
                return;
            }

            CoreWebView2? core = browser.CoreWebView2;
            if (core == null)
            {
                throw new InvalidOperationException("CoreWebView2 初始化后仍为空");
            }

            ConfigureCoreWebView2(core);
            AttachCoreWebView2Handlers(core);

            _isInitialized = true;
            UpdateBrowserVisibility();

            // A URL typed while the engine was still warming up wins over the default.
            string? pendingTarget = _pendingNavigationUrl;
            _pendingNavigationUrl = null;
            string? target = pendingTarget ?? initialUrl;
            StartSurface.Visibility = Visibility.Collapsed;
            BrowserProgressBar.Visibility = Visibility.Collapsed;
            if (SessionStateText != null) SessionStateText.Text = "临时会话已启动";
            SetHubStatus("浏览数据保存在当前临时会话中。");
            LastInitializationError = string.Empty;
            Interlocked.Increment(ref _initializedTabCount);
            AppLog.Info("BrowserTabControl: WebView2 初始化成功");
            if (TryCreateWebUri(target, out Uri? uri) && uri != null)
            {
                NavigateTo(uri);
            }
        }
        catch (Exception ex)
        {
            if (_isDisposed) return;
            _isInitialized = false;
            LastInitializationError = BrowserProfileManager.DescribeErrorCode(ex);
            AppLog.Error("BrowserTabControl.InitializeAsync failed", ex);
            ShowKernelUnavailableState(ex);
        }
        finally
        {
            _isInitializing = false;
            if (!_isDisposed)
            {
                SearchHomeBtn.IsEnabled = true;
                ScheduleBackgroundSleep();
            }
            _initializationCompletion?.TrySetResult(true);
        }
    }

    private WebView2 CreateBrowserControl()
    {
        var browser = new WebView2();
        browser.NavigationStarting += BrowserControl_NavigationStarting;
        browser.NavigationCompleted += BrowserControl_NavigationCompleted;
        BrowserHost.Children.Add(browser);
        browser.AddHandler(PointerPressedEvent, new PointerEventHandler(BrowserPointerPressed), true);
        return _browserControl = browser;
    }

    private static void ConfigureCoreWebView2(CoreWebView2 core)
    {
        try
        {
            CoreWebView2Settings settings = core.Settings;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.AreDevToolsEnabled = false;
            ApplyBrowserColorScheme(core);
            // Resolve a writable local folder before a download starts. The system
            // Downloads folder may be redirected to an unavailable network share.
            Directory.CreateDirectory(AppPaths.IsolatedDownloadsRoot);
            AppPaths.Hide(AppPaths.IsolatedDownloadsRoot);
            core.Profile.DefaultDownloadFolderPath = AppPaths.IsolatedDownloadsRoot;
        }
        catch (Exception ex)
        {
            AppLog.Error("BrowserTabControl: 配置 WebView2 设置失败", ex);
        }
    }

    internal async Task<bool> RunSmokeNavigationAsync(bool checkSleep = false)
    {
        if (checkSleep) _backgroundIdleDelay = TimeSpan.FromSeconds(2);
        await InitializeAsync(null).WaitAsync(TimeSpan.FromSeconds(12));
        WebView2? browser = _browserControl;
        CoreWebView2? core = browser?.CoreWebView2;
        if (!_isInitialized || browser == null || core == null) return false;

        const string title = "StealthWorkspace Browser Check";
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess && core.DocumentTitle == title) loaded.TrySetResult(true);
        }

        browser.NavigationCompleted += OnCompleted;
        try
        {
            const string workPage = "<input id='check'><script>" +
                "setInterval(() => { const start = performance.now(); while (performance.now() - start < 5) {} }, 25);</script>";
            core.NavigateToString("<!doctype html><title>" + title + "</title><h1>Browser ready</h1>" + (checkSleep ? workPage : ""));
            if (!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(8))) return false;
            if (checkSleep)
            {
                await core.ExecuteScriptAsync("document.getElementById('check').value = 'unsaved value';");
            }
            return true;
        }
        finally
        {
            browser.NavigationCompleted -= OnCompleted;
        }
    }

    internal async Task<(bool Suspended, double AwakeCpuMs, double AsleepCpuMs)> CheckSmokeBackgroundSleepAsync()
    {
        CoreWebView2 core = _hookedCore ?? throw new InvalidOperationException();
        // Measure browser renderer CPU time without running script in the sleeping view.
        // The isolated fixture is the only page doing work during these samples.
        async Task<double> SampleRendererCpuAsync()
        {
            TimeSpan TotalCpu()
            {
                TimeSpan sum = TimeSpan.Zero;
                foreach (CoreWebView2ProcessInfo info in core.Environment.GetProcessInfos())
                {
                    if (info.Kind != CoreWebView2ProcessKind.Renderer) continue;
                    try
                    {
                        using var process = System.Diagnostics.Process.GetProcessById((int)info.ProcessId);
                        sum += process.TotalProcessorTime;
                    }
                    catch (ArgumentException) { } // Renderer exited during enumeration.
                    catch (InvalidOperationException) { }
                }
                return sum;
            }
            TimeSpan start = TotalCpu();
            await Task.Delay(400);
            return Math.Max(0, (TotalCpu() - start).TotalMilliseconds);
        }

        double awake = await SampleRendererCpuAsync();
        // Include the product's two 5-second retries: a cold runtime can decline
        // the first request. The prior 5-second deadline expired before retry #1.
        for (int i = 0; i < 200 && !core.IsSuspended && !_backgroundMemoryReduced; i++) await Task.Delay(100);
        return core.IsSuspended ? (true, awake, await SampleRendererCpuAsync()) : (false, awake, double.NaN);
    }

    internal async Task<bool> VerifySmokeResumeAsync()
    {
        if (_hookedCore is not { IsSuspended: false } core) return false;
        string result = await core.ExecuteScriptAsync("document.getElementById('check').value")
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return result == "\"unsaved value\"";
    }

    internal async Task<bool> HasSmokeCookieAsync(bool create = false)
    {
        const string host = "browser-check.invalid";
        const string name = "session-check";
        CoreWebView2CookieManager? manager = _browserControl?.CoreWebView2?.CookieManager;
        if (manager == null) return false;
        if (create) manager.AddOrUpdateCookie(manager.CreateCookie(name, "1", host, "/"));
        var cookies = await manager.GetCookiesAsync("https://" + host + "/");
        return cookies.Any(cookie => cookie.Name == name && cookie.Value == "1");
    }

    private void ShowKernelUnavailableState(Exception exception)
    {
        try
        {
            BrowserProgressBar.Visibility = Visibility.Collapsed;
            StartSurface.Visibility = Visibility.Visible;
            if (_browserControl is { } browser) browser.Visibility = Visibility.Collapsed;
            StartMessage.Text = "可重试，或输入另一个地址。";
            if (SessionStateText != null) SessionStateText.Text = "浏览器启动失败";
            string detail = BrowserProfileManager.LastInitializationError;
            if (string.IsNullOrEmpty(detail))
            {
                detail = BrowserProfileManager.DescribeRuntimeProblem(exception);
            }
            ShowBrowserError("无法启动浏览器", detail);
        }
        catch (Exception ex)
        {
            AppLog.Error("ShowKernelUnavailableState failed", ex);
        }
    }

    /// <summary>Re-initializes (or just reloads) the current document from the UI.</summary>
    private void ReloadCurrentPage()
    {
        if (_isDisposed) return;

        if (!_isInitialized || _browserControl?.CoreWebView2 == null || _navigationFailed)
        {
            if (TryCreateWebUri(_lastRequestedUrl, out Uri? retry) && retry != null) NavigateTo(retry);
            return;
        }

        try
        {
            _browserControl.Reload();
        }
        catch (Exception ex)
        {
            AppLog.Error("Reload failed", ex);
            SetHubStatus("刷新失败，正在尝试重新初始化内核");
            _isInitialized = false;
            _ = InitializeAsync(_lastRequestedUrl);
        }
    }

    #endregion

    #region WebView2 event wiring

    private void AttachCoreWebView2Handlers(CoreWebView2 core)
    {
        if (ReferenceEquals(_hookedCore, core)) return;

        DetachCoreWebView2Handlers();
        _hookedCore = core;

        try
        {
            core.PermissionRequested += CoreWebView2_PermissionRequested;
            core.IsDocumentPlayingAudioChanged += CoreWebView2_IsDocumentPlayingAudioChanged;
            core.DownloadStarting += CoreWebView2_DownloadStarting;
            core.ContainsFullScreenElementChanged += CoreWebView2_ContainsFullScreenElementChanged;
            core.NewWindowRequested += CoreWebView2_NewWindowRequested;
            core.ContextMenuRequested += CoreWebView2_ContextMenuRequested;
            core.WindowCloseRequested += CoreWebView2_WindowCloseRequested;
            core.ProcessFailed += CoreWebView2_ProcessFailed;
            core.FaviconChanged += CoreWebView2_FaviconChanged;
            core.DocumentTitleChanged += CoreWebView2_DocumentTitleChanged;
            core.HistoryChanged += CoreWebView2_HistoryChanged;
            core.SourceChanged += CoreWebView2_SourceChanged;
            core.DOMContentLoaded += CoreWebView2_DOMContentLoaded;
        }
        catch (Exception ex)
        {
            AppLog.Error("AttachCoreWebView2Handlers failed", ex);
        }
    }

    private void DetachCoreWebView2Handlers()
    {
        CancelResponseProbe();
        CancelFaviconRefresh();
        DetachFind();
        DetachImageReader();
        DetachNativeZoom();
        CoreWebView2? core = _hookedCore;
        _hookedCore = null;
        if (core == null) return;

        try
        {
            core.PermissionRequested -= CoreWebView2_PermissionRequested;
            core.IsDocumentPlayingAudioChanged -= CoreWebView2_IsDocumentPlayingAudioChanged;
            core.DownloadStarting -= CoreWebView2_DownloadStarting;
            core.ContainsFullScreenElementChanged -= CoreWebView2_ContainsFullScreenElementChanged;
            core.NewWindowRequested -= CoreWebView2_NewWindowRequested;
            core.ContextMenuRequested -= CoreWebView2_ContextMenuRequested;
            core.WindowCloseRequested -= CoreWebView2_WindowCloseRequested;
            core.ProcessFailed -= CoreWebView2_ProcessFailed;
            core.FaviconChanged -= CoreWebView2_FaviconChanged;
            core.DocumentTitleChanged -= CoreWebView2_DocumentTitleChanged;
            core.HistoryChanged -= CoreWebView2_HistoryChanged;
            core.SourceChanged -= CoreWebView2_SourceChanged;
            core.DOMContentLoaded -= CoreWebView2_DOMContentLoaded;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"DetachCoreWebView2Handlers failed: {ex.GetType().Name}");
        }
    }

    private void CoreWebView2_ProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        if (_isDisposed || _hookedCore is not { } core) return;
        if (SmokeTest.UiSmoke) SmokeTest.Report("browser: ProcessFailed=" + args.ProcessFailedKind);
        if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            // WinRT event senders may use a different projection wrapper. Keep
            // our registered core identity for the asynchronous response guard.
            OnRendererUnresponsive(core);
            return;
        }
        if (args.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or
            CoreWebView2ProcessFailedKind.RenderProcessExited)) return;
        CancelResponseProbe();
        bool browserExited = args.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited;
        _needsRecreate = browserExited;
        _isInitialized = !browserExited;
        _navigationFailed = false;
        _documentReady = false;
        _isPlayingAudio = false;
        _isBrowserFullScreen = false;
        if (browserExited)
        {
            // These native operations cannot resume after their browser process
            // has gone. Release hidden download owners and show a terminal state.
            foreach (var download in _activeDownloads.ToArray())
            {
                if (_downloadRecords.TryGetValue(download, out var record)) record.Detach("浏览器进程已退出，请重新下载");
                DetachDownloadStateHandler(download);
            }
        }
        _activeNavigationId = null;
        UpdateNavigationVisuals();
        _backgroundTimer?.Stop();
        AudioPlayingChanged?.Invoke(false);
        FullScreenModeChanged?.Invoke(false);
        TopNavBar.Visibility = Visibility.Visible;
        BrowserProgressBar.Visibility = Visibility.Collapsed;
        ShowBrowserError(browserExited ? "浏览器进程已退出" : "页面进程异常", browserExited
            ? "点击重试可重新打开。登录状态和未提交内容可能丢失，未完成的下载需重新开始。"
            : "点击重试可重新打开此页面。未提交的网页内容可能无法恢复。");
        AppLog.Warn($"WebView2 process failure: {args.ProcessFailedKind}");
    }

    internal async Task<bool> CheckSmokeCrashRecoveryAsync()
    {
        await using var fixture = new BrowserHttpFixture();
        await NavigateAuditAsync(fixture.Address + "/page");
        var core = _hookedCore;
        if (core == null) return false;
        try { await core.CallDevToolsProtocolMethodAsync("Page.crash", "{}").AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) { SmokeTest.Report("browser: crash request ended=" + ex.GetType().Name); }
        for (int i = 0; i < 50 && !BrowserErrorBar.IsOpen; i++) await Task.Delay(100);
        if (_needsRecreate || !_isInitialized || !BrowserErrorBar.IsOpen) return false;
        var ready = new TaskCompletionSource<bool>();
        void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => ready.TrySetResult(args.IsSuccess);
        core.NavigationCompleted += Completed;
        try
        {
            ReloadCurrentPage();
            if (!await ready.Task.WaitAsync(TimeSpan.FromSeconds(10))) return false;
        }
        finally { core.NavigationCompleted -= Completed; }
        return _isInitialized && ReferenceEquals(core, _hookedCore) && !BrowserErrorBar.IsOpen && await RunSmokeNavigationAsync();
    }

    internal async Task<bool> CheckSmokeHttpAsync()
    {
        await using var fixture = new BrowserHttpFixture();
        var browser = _browserControl!;
        foreach (string route in new[] { "/page", "/challenge", "/missing", "/redirect" })
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Completed(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => completion.TrySetResult(true);
            browser.NavigationCompleted += Completed;
            try
            {
                NavigateTo(new Uri(fixture.Address + route));
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(8));
                bool ok = !BrowserErrorBar.IsOpen && _hookedCore?.DocumentTitle == "HTTP fixture";
                SmokeTest.Report($"browser: HTTP {route}, no false network error={ok}");
                if (!ok) return false;
            }
            finally { browser.NavigationCompleted -= Completed; }
        }
        var domReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fullyLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Ready(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args) => domReady.TrySetResult(true);
        void Loaded(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => fullyLoaded.TrySetResult(true);
        _hookedCore!.DOMContentLoaded += Ready;
        browser.NavigationCompleted += Loaded;
        try
        {
            NavigateTo(new Uri(fixture.Address + "/slowpage"));
            await domReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bool responsive = _documentReady && BrowserProgressBar.Visibility == Visibility.Collapsed && _activeNavigationId != null && UrlTextBox.Text == fixture.Address + "/slowpage";
            SmokeTest.Report("browser: DOM ready hides progress and updates address before slow resource completes=" + responsive);
            if (!responsive) return false;
            await fullyLoaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await _hookedCore.ExecuteScriptAsync("history.pushState(null,'','/ready');");
            for (int i = 0; i < 20 && UrlTextBox.Text != fixture.Address + "/ready"; i++) await Task.Delay(50);
            bool currentAddress = UrlTextBox.Text == fixture.Address + "/ready";
            SmokeTest.Report("browser: SPA navigation updates address=" + currentAddress);
            if (!currentAddress) return false;
        }
        finally { _hookedCore.DOMContentLoaded -= Ready; browser.NavigationCompleted -= Loaded; }

        CoreWebView2DownloadOperation? operation = null;
        void Download(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args) => operation = args.DownloadOperation;
        _hookedCore!.DownloadStarting += Download;
        try
        {
            await _hookedCore.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                "{\"expression\":\"document.getElementById('download').click()\",\"userGesture\":true}");
            for (int i = 0; i < 100 && (operation == null || operation.State == CoreWebView2DownloadState.InProgress); i++) await Task.Delay(100);
            bool ok = operation?.State == CoreWebView2DownloadState.Completed &&
                Path.GetDirectoryName(operation.ResultFilePath) == AppPaths.IsolatedDownloadsRoot &&
                File.ReadAllText(operation.ResultFilePath) == "synthetic download check";
            SmokeTest.Report("browser: isolated download completed with matching content=" + ok);
            if (!ok) SmokeTest.Report($"browser: download diagnostic state={operation?.State}, reason={operation?.InterruptReason}, last request={fixture.LastRequest}");
            if (ok)
            {
                ShowDownloads_Click(this, new RoutedEventArgs());
                await Task.Delay(200);
                ok = _downloadsFlyout?.Content is DownloadListControl { IsLoaded: true } &&
                    BrowserDownloads.Items.Any(item => item.FilePath == operation!.ResultFilePath && item.IsCompleted);
                SmokeTest.Report("browser: shared download manager opens with completed record=" + ok);
                _downloadsFlyout?.Hide();
            }
            return ok;
        }
        finally { _hookedCore.DownloadStarting -= Download; }
    }

    private void CoreWebView2_IsDocumentPlayingAudioChanged(object? sender, object e)
    {
        if (_isDisposed) return;

        try
        {
            _isPlayingAudio = _hookedCore?.IsDocumentPlayingAudio ?? false;
            if (_isPlayingAudio) ResumeBrowser();
            ScheduleBackgroundSleep();
            AudioPlayingChanged?.Invoke(_isPlayingAudio);
            UpdateAudioIcon();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"IsDocumentPlayingAudioChanged handler failed: {ex.GetType().Name}");
        }
    }

    private void CoreWebView2_ContainsFullScreenElementChanged(object? sender, object e)
    {
        if (_isDisposed) return;

        try
        {
            _isBrowserFullScreen = _hookedCore?.ContainsFullScreenElement ?? false;
            if (_isBrowserFullScreen && !_isSelected)
            {
                ExitPageFullScreen();
                return;
            }
            ScheduleBackgroundSleep();
            TopNavBar.Visibility = _isBrowserFullScreen ? Visibility.Collapsed : Visibility.Visible;
            FullScreenModeChanged?.Invoke(_isBrowserFullScreen);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"ContainsFullScreenElementChanged handler failed: {ex.GetType().Name}");
        }
    }

    #endregion

    #region Permissions (CoreWebView2)

    private void CoreWebView2_PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (_isDisposed) return;

        // Always read the source of truth. The ToggleSwitch instances may lag behind
        // SettingsService (and they only write settings back, never drive the decision).
        AppSettingsData cfg = SettingsService.Instance.Data;
        bool shouldBlock = e.PermissionKind switch
        {
            CoreWebView2PermissionKind.Camera => cfg.BlockCamera,
            CoreWebView2PermissionKind.Microphone => cfg.BlockMicrophone,
            CoreWebView2PermissionKind.Geolocation => cfg.BlockGeolocation,
            CoreWebView2PermissionKind.Notifications => cfg.BlockNotifications,
            CoreWebView2PermissionKind.ClipboardRead => true,
            _ => true,
        };

        if (!shouldBlock) return;

        e.State = CoreWebView2PermissionState.Deny;
        e.Handled = true;

        _blockedCounter++;
        ShieldBlockedBadge.Visibility = Visibility.Visible;
        ShieldBlockedCountText.Text = _blockedCounter.ToString(CultureInfo.InvariantCulture);
        SetHubStatus($"已拦截传感器/权限探测请求 ({_blockedCounter})");
        AppLog.Info($"PermissionRequested denied: {e.PermissionKind}");
    }

    private static void ApplyBrowserColorScheme(CoreWebView2 core)
    {
        try
        {
            core.Profile.PreferredColorScheme = SettingsService.Instance.Data.AppTheme switch
            {
                "Dark" => CoreWebView2PreferredColorScheme.Dark,
                "Light" => CoreWebView2PreferredColorScheme.Light,
                _ => CoreWebView2PreferredColorScheme.Auto
            };
        }
        catch (Exception ex) { AppLog.Warn("Browser color scheme unavailable: " + ex.GetType().Name); }
    }

    private void LoadPermissionSettings()
    {
        if (_isUpdatingToggles || _isDisposed) return;
        if (_hookedCore is { } core) ApplyBrowserColorScheme(core);
        if (CameraBlockToggle == null || MicBlockToggle == null || GeoBlockToggle == null || NotifyBlockToggle == null)
        {
            return;
        }

        _isUpdatingToggles = true;
        try
        {
            AppSettingsData cfg = SettingsService.Instance.Data;
            CameraBlockToggle.IsOn = cfg.BlockCamera;
            MicBlockToggle.IsOn = cfg.BlockMicrophone;
            GeoBlockToggle.IsOn = cfg.BlockGeolocation;
            NotifyBlockToggle.IsOn = cfg.BlockNotifications;
        }
        finally
        {
            _isUpdatingToggles = false;
        }
    }

    private void PermissionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingToggles || _isDisposed) return;
        if (CameraBlockToggle == null || MicBlockToggle == null || GeoBlockToggle == null || NotifyBlockToggle == null)
        {
            return;
        }

        // The toggles are only a UI mirror; always persist into SettingsService.
        AppSettingsData cfg = SettingsService.Instance.Data;
        cfg.BlockCamera = CameraBlockToggle.IsOn;
        cfg.BlockMicrophone = MicBlockToggle.IsOn;
        cfg.BlockGeolocation = GeoBlockToggle.IsOn;
        cfg.BlockNotifications = NotifyBlockToggle.IsOn;
        SettingsService.Instance.SaveSettings();
    }

    private void SubscribeSettings()
    {
        if (_settingsSubscribed || _isDisposed) return;
        SettingsService.Instance.SettingsChanged += OnGlobalSettingsChanged;
        _settingsSubscribed = true;
    }

    private void UnsubscribeSettings()
    {
        if (!_settingsSubscribed) return;
        SettingsService.Instance.SettingsChanged -= OnGlobalSettingsChanged;
        _settingsSubscribed = false;
    }

    private void OnGlobalSettingsChanged()
    {
        if (_isDisposed) return;

        DispatcherQueue? dispatcher = this.DispatcherQueue;
        if (dispatcher == null || dispatcher.HasThreadAccess)
        {
            LoadPermissionSettings();
            UpdateBookmarkStarState();
            RefreshStartBookmarks();
            return;
        }

        dispatcher.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            LoadPermissionSettings();
            UpdateBookmarkStarState();
            RefreshStartBookmarks();
        });
    }

    #endregion

    #region Downloads Interception

    private void CoreWebView2_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        if (_isDisposed || _retainedForDownloads)
        {
            e.Cancel = true;
            e.Handled = true;
            return;
        }

        BrowserDownloadPath? reservation = null;
        try
        {
            string isolatedDirectory = AppPaths.IsolatedDownloadsRoot;
            Directory.CreateDirectory(isolatedDirectory);
            AppPaths.Hide(isolatedDirectory);

            string fileName = Path.GetFileName(e.ResultFilePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "isolated_download.bin";
            }

            reservation = BrowserDownloadPath.Reserve(isolatedDirectory, fileName);
            string targetPath = reservation.FilePath;
            e.ResultFilePath = targetPath;
            CoreWebView2DownloadOperation operation = e.DownloadOperation;
            // The shared download list stays available from every tab, including
            // a blank one. Suppress the separate per-WebView native popup.
            e.Handled = true;
            _downloadNavigationUrl = operation.Uri;
            if (string.Equals(_downloadNavigationUrl, _lastRequestedUrl, StringComparison.Ordinal))
            {
                BrowserErrorBar.IsOpen = false;
                _navigationFailed = false;
            }

            string displayName = Path.GetFileName(targetPath);
            DownloadToastTitle.Text = $"正在隔离下载: {displayName}";
            DownloadToastDesc.Text = "文件保存在应用隐藏隔离目录，未进入系统下载夹";
            DownloadToast.Visibility = Visibility.Visible;
            SetHubStatus("下载已重定向到应用隐藏隔离目录");
            AppLog.Info("DownloadStarting: 下载已重定向到应用隐藏隔离目录");

            _downloadPaths.Add(operation, reservation);
            reservation = null;
            _displayedDownload = operation;
            _downloadProgressUpdatedAt = 0;
            _downloadRecords.Add(operation, BrowserDownloads.Add(operation, targetPath, ResumeBrowser));
            AttachDownloadStateHandler(operation);
            string? requested = _lastRequestedUrl;
            DispatcherQueue.TryEnqueue(() =>
            {
                // A download does not replace the displayed document. Restore its
                // address, but never overwrite a new address the user is editing.
                if (_isDisposed || _lastRequestedUrl != requested || UrlTextBox.Text != requested) return;
                string source = GetCurrentDocumentUrl();
                if (TryCreateWebUri(source, out _) && source != requested) UrlTextBox.Text = source;
            });
        }
        catch (Exception ex)
        {
            // If the isolated directory cannot be prepared, cancel instead of letting
            // the browser fall back to the system Downloads folder.
            AppLog.Error("DownloadStarting: 隔离目录不可用，已阻止下载", ex);
            try
            {
                e.Cancel = true;
                e.Handled = true;
            }
            catch
            {
                // ignored
            }

            try
            {
                DownloadToastTitle.Text = "下载已阻止";
                DownloadToastDesc.Text = "无法创建应用隔离目录，文件没有被写入系统下载夹";
                DownloadToast.Visibility = Visibility.Visible;
            }
            catch
            {
                // ignored
            }
        }
        finally { reservation?.Dispose(); }
    }

    private void AttachDownloadStateHandler(CoreWebView2DownloadOperation? operation)
    {
        if (operation == null || !_activeDownloads.Add(operation)) return;
        // WinRT can project the same native operation into different wrappers for
        // event senders. Always pass the captured registration object to tracking.
        Windows.Foundation.TypedEventHandler<CoreWebView2DownloadOperation, object> state = (_, args) => OnDownloadStateChanged(operation, args);
        Windows.Foundation.TypedEventHandler<CoreWebView2DownloadOperation, object> progress = (_, args) => OnDownloadProgressChanged(operation, args);
        _downloadHandlers.Add(operation, (state, progress));
        operation.StateChanged += state;
        operation.BytesReceivedChanged += progress;
        ResumeBrowser();
        ScheduleBackgroundSleep();
    }

    private void DetachDownloadStateHandler(CoreWebView2DownloadOperation operation)
    {
        if (!_activeDownloads.Remove(operation)) return;

        try
        {
            if (_downloadHandlers.Remove(operation, out var handlers))
            {
                operation.StateChanged -= handlers.State;
                operation.BytesReceivedChanged -= handlers.Progress;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"DetachDownloadStateHandler failed: {ex.GetType().Name}");
        }
        if (_downloadPaths.Remove(operation, out var reservation)) reservation.Dispose();
        if (_downloadRecords.Remove(operation, out var record)) record.Detach();
        if (ReferenceEquals(_displayedDownload, operation)) _displayedDownload = null;
        ScheduleBackgroundSleep();
        DownloadsActivityChanged?.Invoke();
    }

    private void OnDownloadStateChanged(object? sender, object e)
    {
        if (sender is not CoreWebView2DownloadOperation operation) return;

        if (_isDisposed)
        {
            DetachDownloadStateHandler(operation);
            return;
        }

        try
        {
            if (_downloadRecords.TryGetValue(operation, out var record)) record.Update(operation, true);
            switch (operation.State)
            {
                case CoreWebView2DownloadState.Completed:
                    DetachDownloadStateHandler(operation);
                    DownloadToastTitle.Text = "下载完成（已隔离）";
                    DownloadToastDesc.Text = "文件保存在应用隐藏隔离目录，未进入系统下载夹";
                    DownloadToast.Visibility = Visibility.Visible;
                    AppLog.Info("Download: 已下载到应用隐藏隔离目录");
                    break;

                case CoreWebView2DownloadState.Interrupted:
                    // Current runtimes report UserPaused; older versions may use
                    // UserCanceled. Keep resumable operations attached in both cases.
                    if (!operation.CanResume) DetachDownloadStateHandler(operation);
                    else ScheduleBackgroundSleep();
                    DownloadToastTitle.Text = operation.CanResume && Models.BrowserDownloadItem.IsUserPause(operation.InterruptReason)
                        ? "下载已暂停" : "下载已中断";
                    DownloadToastDesc.Text = operation.CanResume ? "可以在下载列表中继续" : "下载未完成，可重新下载";
                    DownloadToast.Visibility = Visibility.Visible;
                    SetHubStatus($"下载被中断（{operation.InterruptReason}）");
                    AppLog.Warn($"Download interrupted: {operation.InterruptReason}");
                    break;

                case CoreWebView2DownloadState.InProgress:
                    ResumeBrowser();
                    ScheduleBackgroundSleep();
                    _displayedDownload = operation;
                    _downloadProgressUpdatedAt = 0;
                    DownloadToastTitle.Text = "正在下载：" + Path.GetFileName(operation.ResultFilePath);
                    OnDownloadProgressChanged(operation, e);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"OnDownloadStateChanged failed: {ex.GetType().Name}");
        }
    }

    private void OnDownloadProgressChanged(object? sender, object args)
    {
        if (_isDisposed || sender is not CoreWebView2DownloadOperation operation) return;
        try
        {
            if (_downloadRecords.TryGetValue(operation, out var record)) record.Update(operation, false);
            if (!ReferenceEquals(operation, _displayedDownload) || DownloadToast.Visibility != Visibility.Visible) return;
            long now = Environment.TickCount64;
            if (now - _downloadProgressUpdatedAt < 250) return;
            _downloadProgressUpdatedAt = now;
            DownloadToastDesc.Text = operation.TotalBytesToReceive > 0
                ? $"{operation.BytesReceived * 100.0 / operation.TotalBytesToReceive:0}% · 保存在应用下载文件夹"
                : $"已接收 {operation.BytesReceived / 1048576.0:0.0} MB · 保存在应用下载文件夹";
        }
        catch (Exception ex) { AppLog.Warn("Download progress: " + ex.GetType().Name); }
    }

    private void DismissDownloadToast_Click(object sender, RoutedEventArgs e)
    {
        DownloadToast.Visibility = Visibility.Collapsed;
    }

    #endregion

    #region Shortcuts, Zoom & Find

    private void BrowserTabControl_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isDisposed) return;
        if (_nativeFindDispatching && (e.Key is VirtualKey.F or VirtualKey.F3)) return;

        bool isCtrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (isCtrl && e.Key == VirtualKey.J) { e.Handled = true; ShowDownloads_Click(this, new RoutedEventArgs()); return; }
        bool isAlt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

        if (isCtrl && e.Key == VirtualKey.D)
        {
            BookmarkStarBtn_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && FindBar.Visibility == Visibility.Visible)
        {
            CloseFindBar();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F3)
        {
            bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            FindFromShortcut(advance: true, backwards: shift);
            e.Handled = true;
        }
        else if (!isAlt && (e.Key == VirtualKey.F5 || isCtrl && e.Key == VirtualKey.R))
        {
            bool shift = IsKeyPressed(VirtualKey.Shift);
            if (shift || isCtrl && e.Key == VirtualKey.F5) _ = ReloadIgnoringCacheAsync();
            else ReloadCurrentPage();
            e.Handled = true;
        }
        else if (isAlt && e.Key == VirtualKey.Left)
        {
            NavigateBack();
            e.Handled = true;
        }
        else if (isAlt && e.Key == VirtualKey.Right)
        {
            NavigateForward();
            e.Handled = true;
        }
        else if (isCtrl && e.Key == VirtualKey.F)
        {
            ToggleFindBar();
            e.Handled = true;
        }
        else if (isCtrl && !_nativeZoomDispatching && _browserControl?.FocusState == FocusState.Unfocused &&
            e.Key is (VirtualKey)187 or VirtualKey.Add or (VirtualKey)189 or VirtualKey.Subtract or VirtualKey.Number0 or VirtualKey.NumberPad0)
        {
            // The page handles its own zoom shortcuts. Forward only when the
            // address bar or app chrome owns focus, avoiding a second CSS zoom.
            ushort key = e.Key is (VirtualKey)187 or VirtualKey.Add ? (ushort)0xBB :
                e.Key is (VirtualKey)189 or VirtualKey.Subtract ? (ushort)0xBD : (ushort)0x30;
            _ = SendNativeZoomAsync(key);
            e.Handled = true;
        }
    }

    #endregion

    #region Bookmarks

    private void UpdateBookmarkStarState()
    {
        if (_isDisposed) return;

        string currentUrl = GetCurrentDocumentUrl();
        BookmarkStarBtn.IsEnabled = TryCreateWebUri(currentUrl, out _);
        bool bookmarked = BookmarkStarBtn.IsEnabled && SettingsService.Instance.IsBookmarked(currentUrl);
        string action = bookmarked ? "取消收藏" : "收藏本页";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BookmarkStarBtn, action);
        ToolTipService.SetToolTip(BookmarkStarBtn, action + " (Ctrl+D)");

        if (bookmarked)
        {
            BookmarkStarIcon.Glyph = "\uE735";
            BookmarkStarIcon.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 245, 158, 11)); // Amber
        }
        else
        {
            BookmarkStarIcon.Glyph = "\uE734";
            BookmarkStarIcon.ClearValue(FontIcon.ForegroundProperty);
        }
    }

    private void BookmarkStarBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isDisposed) return;

        string currentUrl = GetCurrentDocumentUrl();
        if (string.IsNullOrEmpty(currentUrl) || IsBlankDocumentUrl(currentUrl)) return;

        bool bookmarked = SettingsService.Instance.IsBookmarked(currentUrl);
        bool saved;
        if (bookmarked)
        {
            saved = SettingsService.Instance.RemoveBookmark(currentUrl);
        }
        else
        {
            string title = string.IsNullOrWhiteSpace(CurrentTitle) ? currentUrl : CurrentTitle;
            saved = SettingsService.Instance.AddBookmark(title, currentUrl);
        }

        UpdateBookmarkStarState();
        if (!saved) ShowBookmarkSaveError();
    }

    private void BookmarksFlyout_Opening(object? sender, object e)
    {
        BookmarkSearchBox.Text = "";
        RefreshBookmarkList();
    }

    private void BookmarkItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BookmarkItem bookmark && !string.IsNullOrEmpty(bookmark.Url))
        {
            if (IsKeyPressed(VirtualKey.Control)) NewTabRequested?.Invoke(bookmark.Url, !IsKeyPressed(VirtualKey.Shift));
            else NavigateToText(bookmark.Url);
            BookmarksFlyout.Hide();
        }
    }

    private void BookmarkDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BookmarkItem bookmark)
        {
            bool saved = SettingsService.Instance.RemoveBookmark(bookmark.Url);
            RefreshBookmarkList();
            UpdateBookmarkStarState();
            if (!saved) { BookmarksFlyout.Hide(); ShowBookmarkSaveError(); }
        }
    }

    #endregion

    #region Audio Indicator & Mute

    internal void ToggleAudioMute() => AudioMuteBtn_Click(this, new RoutedEventArgs());

    private void UpdateAudioIcon()
    {
        CoreWebView2? core = _browserControl?.CoreWebView2;
        bool isMuted = core != null && core.IsMuted;
        AudioMuteBtn.IsEnabled = core != null;
        AudioMuteBtn.Content = isMuted ? "取消静音" : "静音标签页";
        NavigationVisualChanged?.Invoke();
    }

    private void AudioMuteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isDisposed) return;

        CoreWebView2? core = _browserControl?.CoreWebView2;
        if (core == null) return;

        try
        {
            core.IsMuted = !core.IsMuted;
            UpdateAudioIcon();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"AudioMuteBtn_Click failed: {ex.GetType().Name}");
        }
    }

    #endregion

    #region Privacy Hub Flyout

    private void SetActiveTab(Button activeButton, FrameworkElement activePanel)
    {
        try
        {
            var accentStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
            var subtleStyle = (Style)Application.Current.Resources["SubtleButtonStyle"];

            TabFirewallBtn.Style = subtleStyle;
            TabCookieBtn.Style = subtleStyle;
            TabStorageBtn.Style = subtleStyle;
            TabHistoryBtn.Style = subtleStyle;

            activeButton.Style = accentStyle;

            PanelFirewall.Visibility = Visibility.Collapsed;
            PanelCookie.Visibility = Visibility.Collapsed;
            PanelStorage.Visibility = Visibility.Collapsed;
            PanelHistory.Visibility = Visibility.Collapsed;

            activePanel.Visibility = Visibility.Visible;
            UiMotion.Enter(activePanel);
        }
        catch (Exception ex)
        {
            AppLog.Error("SetActiveTab failed", ex);
        }
    }

    private void TabFirewall_Click(object sender, RoutedEventArgs e) => SetActiveTab(TabFirewallBtn, PanelFirewall);

    private void TabCookie_Click(object sender, RoutedEventArgs e)
    {
        SetActiveTab(TabCookieBtn, PanelCookie);
        _ = RefreshCookiesAsync();
    }

    private void TabStorage_Click(object sender, RoutedEventArgs e) => SetActiveTab(TabStorageBtn, PanelStorage);

    private void TabHistory_Click(object sender, RoutedEventArgs e)
    {
        SetActiveTab(TabHistoryBtn, PanelHistory);
        UpdateEmptyNotices();
    }

    private void CloseFlyout_Click(object sender, RoutedEventArgs e)
    {
        PrivacyFlyout.Hide();
    }

    private void PrivacyFlyout_Opening(object? sender, object e)
    {
        EnsurePrivacyContent();
        string currentUrl = GetCurrentDocumentUrl();
        FlyoutDomainText.Text = DescribeHost(currentUrl);
        SessionStateText.Text = Uri.TryCreate(currentUrl, UriKind.Absolute, out var site) && site.Scheme is "http" or "https"
            ? site.Scheme == "https" ? "HTTPS · 临时会话" : "HTTP · 连接未加密"
            : _isInitialized ? "临时会话已启动" : "尚未打开网页";
        UpdateEmptyNotices();

        if (PanelCookie.Visibility == Visibility.Visible)
        {
            _ = RefreshCookiesAsync();
        }
    }

    private void EnsurePrivacyContent()
    {
        if (PrivacyContent != null) return;
        // Group the whole rarely-used panel behind one placeholder. The permission
        // policy continues to read SettingsService even before this UI exists.
        _isUpdatingToggles = true;
        try { FindName(nameof(PrivacyContent)); }
        finally { _isUpdatingToggles = false; }
        CookieListView.ItemsSource = _filteredCookies;
        HistoryListView.ItemsSource = _sessionHistory;
        SessionStateText.Text = _isInitialized ? "临时会话已启动" : "尚未打开网页";
        HubStatusText.Text = _hubStatus;
        LoadPermissionSettings();
        UpdateEmptyNotices();
    }

    internal async Task<bool> CheckSmokeDeferredPrivacyAsync()
    {
        bool lazy = !HasPrivacyUi;
        var cfg = SettingsService.Instance.Data;
        var policy = (cfg.BlockCamera, cfg.BlockMicrophone, cfg.BlockGeolocation, cfg.BlockNotifications);
        EnsurePrivacyContent();
        var panel = PrivacyContent;
        EnsurePrivacyContent();
        await RefreshCookiesAsync();
        bool ok = lazy && HasPrivacyUi && ReferenceEquals(panel, PrivacyContent) &&
            ReferenceEquals(CookieListView.ItemsSource, _filteredCookies) && ReferenceEquals(HistoryListView.ItemsSource, _sessionHistory) &&
            policy == (cfg.BlockCamera, cfg.BlockMicrophone, cfg.BlockGeolocation, cfg.BlockNotifications);
        SmokeTest.Report("browser: deferred privacy loads once, binds lists, preserves policy=" + ok);
        return ok;
    }

    #endregion

    #region Cookie Management

    private async Task RefreshCookiesAsync()
    {
        if (_isDisposed) return;

        CoreWebView2? core = _browserControl?.CoreWebView2;
        if (core == null)
        {
            SetHubStatus("浏览器内核尚未就绪，无法读取 Cookie");
            return;
        }

        try
        {
            // Prefer CoreWebView2.Source: the XAML control's Source can be stale.
            string currentUrl = GetCurrentDocumentUrl();
            string queryUri = TryCreateWebUri(currentUrl, out Uri? currentUri) && currentUri != null
                ? currentUri.AbsoluteUri
                : string.Empty; // empty => all cookies of the isolated profile

            // Return type is the WinRT-projected cookie collection; 'var' keeps this
            // independent of the exact IReadOnlyList/IVectorView projection.
            var cookies = await core.CookieManager.GetCookiesAsync(queryUri);
            if (_isDisposed) return;

            _allCookies.Clear();
            foreach (CoreWebView2Cookie cookie in cookies)
            {
                _allCookies.Add(new CookieDisplayItem
                {
                    Name = cookie.Name,
                    Domain = cookie.Domain,
                    Value = cookie.Value,
                    Path = cookie.Path,
                    IsSecure = cookie.IsSecure,
                    IsHttpOnly = cookie.IsHttpOnly,
                    DisplayExpires = FormatCookieExpiry(cookie),
                    NativeCookie = cookie,
                });
            }

            FilterCookies();
            SetHubStatus($"隔离会话中共有 {_allCookies.Count} 个 Cookie");
        }
        catch (Exception ex)
        {
            AppLog.Error("RefreshCookiesAsync failed", ex);
            SetHubStatus("读取 Cookie 失败（详情见诊断日志）");
        }
    }

    private static string FormatCookieExpiry(CoreWebView2Cookie cookie)
    {
        try
        {
            if (cookie.IsSession || cookie.Expires <= 0) return "会话结束";
            return DateTimeOffset.FromUnixTimeSeconds((long)cookie.Expires)
                .LocalDateTime
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        catch
        {
            return "会话结束";
        }
    }

    private void FilterCookies()
    {
        string query = CookieSearchBox?.Text?.Trim().ToLowerInvariant() ?? string.Empty;
        _filteredCookies.Clear();

        foreach (CookieDisplayItem cookie in _allCookies)
        {
            if (string.IsNullOrEmpty(query) ||
                cookie.Name.ToLowerInvariant().Contains(query) ||
                cookie.Domain.ToLowerInvariant().Contains(query))
            {
                _filteredCookies.Add(cookie);
            }
        }

        UpdateEmptyNotices();
    }

    private void CookieSearchBox_TextChanged(object sender, TextChangedEventArgs e) => FilterCookies();

    private async void RefreshCookies_Click(object sender, RoutedEventArgs e) => await RefreshCookiesAsync();

    private async void DeleteSingleCookie_Click(object sender, RoutedEventArgs e)
    {
        if (_isDisposed) return;

        CoreWebView2? core = _hookedCore;
        if (core == null) return;

        if ((sender as FrameworkElement)?.Tag is not CookieDisplayItem cookieItem) return;

        try
        {
            await BrowserSiteData.DeleteMatchingCookiesAsync(core,
                cookie => cookie.Name == cookieItem.Name && cookie.Domain == cookieItem.Domain && cookie.Path == cookieItem.Path,
                () => !_isDisposed && ReferenceEquals(core, _hookedCore));
            if (_isDisposed || !ReferenceEquals(core, _hookedCore)) return;
            _allCookies.Remove(cookieItem);
            _filteredCookies.Remove(cookieItem);
            UpdateEmptyNotices();
            SetHubStatus($"已删除 Cookie: {cookieItem.Name}");
            AppLog.Info("CookieManager: 已删除单个 Cookie");
        }
        catch (Exception ex)
        {
            AppLog.Error("DeleteSingleCookie failed", ex);
            SetHubStatus("删除 Cookie 失败（详情见诊断日志）");
        }
    }

    private async void DeleteSiteCookies_Click(object sender, RoutedEventArgs e)
    {
        if (_isDisposed) return;

        CoreWebView2? core = _hookedCore;
        if (core == null) return;

        string currentUrl = GetCurrentDocumentUrl();
        if (!TryCreateWebUri(currentUrl, out Uri? currentUri) || currentUri == null || string.IsNullOrEmpty(currentUri.Host))
        {
            SetHubStatus("当前没有可清理 Cookie 的活动站点");
            return;
        }

        string host = currentUri.IdnHost;
        try
        {
            int count = await BrowserSiteData.DeleteCookiesForHostAsync(core, host,
                () => !_isDisposed && ReferenceEquals(_hookedCore, core));
            if (_isDisposed || !ReferenceEquals(_hookedCore, core)) return;
            await RefreshCookiesAsync();
            SetHubStatus($"已清除 {host} 的 {count} 个 Cookie");
            AppLog.Info("CookieManager: 已清除当前站点的 Cookie");
        }
        catch (Exception ex)
        {
            AppLog.Error("DeleteSiteCookies failed", ex);
            SetHubStatus("清除站点 Cookie 失败（详情见诊断日志）");
        }
    }

    private async void DeleteAllCookies_Click(object sender, RoutedEventArgs e)
    {
        if (_isDisposed) return;

        CoreWebView2? core = _browserControl?.CoreWebView2;
        if (core == null) return;

        try
        {
            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.Cookies);
            if (_isDisposed) return;
            _allCookies.Clear();
            _filteredCookies.Clear();
            UpdateEmptyNotices();
            SetHubStatus("所有 Cookie 已清空");
            AppLog.Info("CookieManager: 已清空全部 Cookie");
        }
        catch (Exception ex)
        {
            AppLog.Error("DeleteAllCookies failed", ex);
            SetHubStatus("清空 Cookie 失败（详情见诊断日志）");
        }
    }

    #endregion

    #region Storage & Cache

    private async void ClearAllStorage_Click(object sender, RoutedEventArgs e)
    {
        if (_isDisposed) return;

        CoreWebView2? core = _browserControl?.CoreWebView2;
        if (core == null) return;

        try
        {
            await core.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.AllDomStorage |
                CoreWebView2BrowsingDataKinds.IndexedDb |
                CoreWebView2BrowsingDataKinds.CacheStorage);

            SetHubStatus("本地存储与缓存已清空（Cookie 需在上方单独清除）");
        }
        catch (Exception ex)
        {
            AppLog.Error("ClearAllStorage failed", ex);
            SetHubStatus("清除存储失败（详情见诊断日志）");
        }
    }

    private async void ClearHttpCache_Click(object sender, RoutedEventArgs e)
    {
        if (_isDisposed) return;

        CoreWebView2? core = _browserControl?.CoreWebView2;
        if (core == null) return;

        try
        {
            await core.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.DiskCache |
                CoreWebView2BrowsingDataKinds.CacheStorage);

            SetHubStatus("网页临时缓存已释放");
        }
        catch (Exception ex)
        {
            AppLog.Error("ClearHttpCache failed", ex);
            SetHubStatus("清理缓存失败（详情见诊断日志）");
        }
    }

    private void CleanReload_Click(object sender, RoutedEventArgs e)
    {
        ReloadCurrentPage();
        SetHubStatus("已重新加载当前页面");
    }

    #endregion

    #region History (volatile, in-memory)

    private void HistoryNavigate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HistorySessionItem item && !string.IsNullOrEmpty(item.Url))
        {
            NavigateToText(item.Url);
            PrivacyFlyout.Hide();
        }
    }

    private void HistoryDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HistorySessionItem item)
        {
            _sessionHistory.Remove(item);
            UpdateEmptyNotices();
        }
    }

    private void ClearSessionHistory_Click(object sender, RoutedEventArgs e)
    {
        _sessionHistory.Clear();
        UpdateEmptyNotices();
        SetHubStatus("临时会话历史已清空");
    }

    private void AddSessionHistory(string title, string url)
    {
        BrowserSessionHistory.Record(title, url);
        UpdateEmptyNotices();
    }

    #endregion

    #region Navigation & Omnibox

    private void RefreshStartBookmarks()
    {
        if (_isDisposed || StartSurface.Visibility != Visibility.Visible) return;
        var bookmarks = SettingsService.Instance.Data.Bookmarks.Take(12).ToList();
        StartBookmarks.ItemsSource = bookmarks;
        StartBookmarksEmpty.Visibility = bookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MoreActionsFlyout_Opening(object? sender, object e)
    {
        UpdateAudioIcon();
        PageImagesBtn.IsEnabled = _isInitialized && _hookedCore != null && _documentReady;
        foreach (var control in ZoomMenuRow.Children.OfType<Control>()) control.IsEnabled = _isInitialized && _hookedCore != null;
        _ = RefreshNativeZoomAsync();
    }

    private void ShowFavorites_Click(object sender, RoutedEventArgs e) => BookmarksFlyout.ShowAt(FavoritesBtn);

    private void ShowHistory_Click(object sender, RoutedEventArgs e)
    {
        MoreActionsFlyout.Hide();
        EnsurePrivacyContent();
        PrivacyFlyout.ShowAt(SiteShieldBtn);
        TabHistory_Click(sender, e);
    }

    private async void OpenDownloadsFolder_Click(object sender, RoutedEventArgs e)
    {
        MoreActionsFlyout.Hide();
        try
        {
            Directory.CreateDirectory(AppPaths.IsolatedDownloadsRoot);
            AppPaths.Hide(AppPaths.IsolatedDownloadsRoot);
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(AppPaths.IsolatedDownloadsRoot);
            if (!await Launcher.LaunchFolderAsync(folder)) ShowBrowserError("无法打开下载文件夹", "请稍后重试。");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Open downloads folder failed: " + ex.GetType().Name);
            ShowBrowserError("无法打开下载文件夹", "文件仍保留在应用下载目录中，请稍后重试。");
        }
    }

    private void ShowDownloads_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _downloadsFlyout ??= new Flyout { Content = new DownloadListControl(), Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
            _downloadsFlyout.ShowAt(DownloadsBtn);
            DownloadToast.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { AppLog.Warn("Download dialog failed: " + ex.GetType().Name); ShowBrowserError("无法显示下载记录", "可以从更多菜单打开下载文件夹。"); }
    }

    private void CoreWebView2_DocumentTitleChanged(CoreWebView2 sender, object args)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(sender.DocumentTitle)) return;
        CurrentTitle = sender.DocumentTitle;
        TitleChanged?.Invoke(CurrentTitle);
        BrowserSessionHistory.UpdateTitle(CurrentTitle, sender.Source);
    }

    private void CoreWebView2_HistoryChanged(CoreWebView2 sender, object args)
    {
        if (_isDisposed) return;
        BackBtn.IsEnabled = sender.CanGoBack;
        ForwardBtn.IsEnabled = sender.CanGoForward;
        if (UrlTextBox.FocusState == FocusState.Unfocused && !IsBlankDocumentUrl(sender.Source)) UrlTextBox.Text = sender.Source;
    }

    private void CoreWebView2_SourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args)
    {
        if (_isDisposed || IsBlankDocumentUrl(sender.Source)) return;
        // Commit the real address on redirects/SPA navigation without waiting for
        // slow images or ads. Preserve an address the user has started editing.
        if (UrlTextBox.FocusState == FocusState.Unfocused || UrlTextBox.Text == _addressAtNavigationStart)
        {
            UrlTextBox.Text = sender.Source;
            _addressAtNavigationStart = sender.Source;
        }
        UpdateBookmarkStarState();
        if (!args.IsNewDocument && _documentReady) AddSessionHistory(sender.DocumentTitle, sender.Source);
    }

    private void CoreWebView2_DOMContentLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args)
    {
        if (_isDisposed || _activeNavigationId != args.NavigationId) return;
        _documentReady = true;
        if (!IsBlankDocumentUrl(sender.Source))
        {
            AddSessionHistory(sender.DocumentTitle, sender.Source);
            _historyRecorded = true;
        }
        BrowserProgressBar.Visibility = Visibility.Collapsed;
        NavigationVisualChanged?.Invoke();
        // A document can be usable while an ad/image stalls the load event for
        // minutes. Let a hidden, inactive tab idle after DOM readiness; the
        // runtime still decides whether suspension is safe for that page.
        ScheduleBackgroundSleep();
    }

    internal async Task<bool> CheckSmokeChromeAsync()
    {
        if (_hookedCore is not { } core) return false;
        byte[] icon = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png"));
        string dataUri = "data:image/png;base64," + Convert.ToBase64String(icon);
        await core.ExecuteScriptAsync("document.title='Updated fixture';var icon=document.createElement('link');icon.rel='icon';icon.href=" + JsonSerializer.Serialize(dataUri) + ";document.head.appendChild(icon);");
        for (int i = 0; i < 30 && (_favicon == null || CurrentTitle != "Updated fixture"); i++) await Task.Delay(100);
        bool visual = _favicon != null && CurrentTitle == "Updated fixture";
        for (int i = 0; i < 60 && _faviconSourcePixels < 128; i++) await Task.Delay(50);
        bool sharpIcon = _faviconSourcePixels >= 128 && _favicon?.PixelWidth == FaviconPixels;
        SmokeTest.Report($"browser: favicon original pixels={_faviconSourcePixels}, rendered pixels={_favicon?.PixelWidth}, DPI target={FaviconPixels}, sharp={sharpIcon}");
        bool canvas = await core.ExecuteScriptAsync("(()=>{try{document.createElement('canvas').getContext('2d').getImageData(0,0,1,1);return true}catch{return false}})()") == "true";
        SmokeTest.Report("browser: standard canvas readback available=" + canvas);
        ToggleAudioMute();
        bool muted = core.IsMuted;
        ToggleAudioMute();
        SmokeTest.Report($"browser: dynamic title + in-memory favicon={visual}; mute toggles={muted && !core.IsMuted}");
        return canvas && sharpIcon && visual && muted && !core.IsMuted;
    }

    private void UpdateNavigationVisuals()
    {
        bool loading = _activeNavigationId != null;
        ReloadStopIcon.Glyph = loading ? "\uE711" : "\uE72C";
        ToolTipService.SetToolTip(ReloadStopBtn, loading ? "停止加载" : "刷新 (F5)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ReloadStopBtn, loading ? "停止加载" : "刷新");
        NavigationVisualChanged?.Invoke();
    }

    private void ReloadStopBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_activeNavigationId != null && _hookedCore is { } core) core.Stop();
            else ReloadCurrentPage();
        }
        catch (Exception ex) { AppLog.Warn("Stop navigation failed: " + ex.GetType().Name); }
    }

    private void UrlTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        SelectAddressText();
    }

    private void UrlTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            if (UrlTextBox.IsSuggestionListOpen) { UrlTextBox.IsSuggestionListOpen = false; e.Handled = true; return; }
            UrlTextBox.Text = IsBlankDocumentUrl(CurrentUrl) ? "" : CurrentUrl;
            _browserControl?.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }
    }

    private void HomeBtn_Click(object sender, RoutedEventArgs e)
    {
        NavigateToText(SettingsService.Instance.GetSearchHomeUrl());
    }

    private void SubmitAddress(string rawInput)
    {
        string text = rawInput.Trim();
        if (string.IsNullOrEmpty(text)) return;

        Uri? uri = BuildNavigableUri(text);
        if (uri == null)
        {
            ShowBrowserError("无法打开此地址", "支持 HTTP/HTTPS 网址，或直接输入搜索关键词。");
            return;
        }
        bool alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (alt) NewTabRequested?.Invoke(uri.AbsoluteUri, false);
        else { _focusPageAfterNavigation = true; NavigateTo(uri); }
    }

    internal void NavigateToText(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        if (TryCreateWebUri(url.Trim(), out Uri? uri) && uri != null)
        {
            NavigateTo(uri);
        }
        else
        {
            SetHubStatus("该地址不是有效的 http/https 链接");
        }
    }

    private void NavigateTo(Uri uri)
    {
        if (_isDisposed) return;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            SetHubStatus("仅支持 http/https 地址");
            return;
        }

        // A deferred blank-tab focus request must not steal keyboard input after
        // a real navigation has begun (notably a fast local or cached page).
        _focusAddressOnLoad = false;

        if (!string.Equals(_lastRequestedUrl, uri.AbsoluteUri, StringComparison.Ordinal) ||
            UrlTextBox.FocusState == FocusState.Unfocused)
        {
            UrlTextBox.Text = uri.AbsoluteUri;
        }

        _lastRequestedUrl = uri.AbsoluteUri;
        CoreWebView2? core = _browserControl?.CoreWebView2;
        if (!_isInitialized || core == null)
        {
            if (_isInitializing)
            {
                // Engine is still warming up: remember the target and navigate once ready.
                _pendingNavigationUrl = uri.AbsoluteUri;
            }
            else
            {
                // Recoverable: retry the initialization with the requested target.
                _ = InitializeAsync(uri.AbsoluteUri);
            }

            return;
        }

        try
        {
            ResumeBrowser();
            core.Navigate(uri.AbsoluteUri);
            if (_focusPageAfterNavigation)
            {
                _focusPageAfterNavigation = false;
                if (_isSelected && UrlTextBox.Text == uri.AbsoluteUri) _browserControl?.Focus(FocusState.Programmatic);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("NavigateTo failed", ex);
            _navigationFailed = true;
            ShowBrowserError("无法打开页面", "请检查地址和网络连接，然后重试。");
        }
    }

    private static Uri? BuildNavigableUri(string rawInput) => BrowserAddress.Resolve(rawInput, SettingsService.Instance.GetSearchUrl);

    private static bool TryCreateWebUri(string? text, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;

        uri = parsed;
        return true;
    }

    private static bool IsBlankDocumentUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        return url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("edge:", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeHost(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "当前无活动站点";
        if (IsBlankDocumentUrl(url)) return "当前无活动站点";

        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host))
            {
                return uri.Host;
            }
        }
        catch
        {
            // ignored
        }

        return "安全沙箱";
    }

    private string GetCurrentDocumentUrl()
    {
        try
        {
            CoreWebView2? core = _browserControl?.CoreWebView2;
            if (core != null)
            {
                string source = core.Source;
                if (!string.IsNullOrWhiteSpace(source)) return source;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"GetCurrentDocumentUrl failed: {ex.GetType().Name}");
        }

        try
        {
            return _browserControl?.Source?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void NavigateBack()
    {
        if (!_isInitialized || _browserControl is not { CoreWebView2: not null } browser) return;

        try
        {
            if (browser.CanGoBack) browser.GoBack();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"NavigateBack failed: {ex.GetType().Name}");
        }
    }

    private void NavigateForward()
    {
        if (!_isInitialized || _browserControl is not { CoreWebView2: not null } browser) return;

        try
        {
            if (browser.CanGoForward) browser.GoForward();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"NavigateForward failed: {ex.GetType().Name}");
        }
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e) => NavigateBack();

    private void ForwardBtn_Click(object sender, RoutedEventArgs e) => NavigateForward();

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => ReloadCurrentPage();

    private void BrowserControl_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (_isDisposed) return;
        CancelResponseProbe();
        StopFind();
        HideZoomPanel();

        if (_activeNavigationId != args.NavigationId)
        {
            _blockedPopupUrls.Clear();
            _blockedPopupCount = 0;
            PopupBlockedBtn.Visibility = Visibility.Collapsed;
            _addressAtNavigationStart = UrlTextBox.Text;
            _activeNavigationId = args.NavigationId;
        }
        _backgroundTimer?.Stop();
        _documentReady = false;
        _historyRecorded = false;
        if (TryCreateWebUri(args.Uri, out Uri? target)) _lastRequestedUrl = target!.AbsoluteUri;
        _downloadNavigationUrl = null;
        _navigationFailed = false;
        BrowserErrorBar.IsOpen = false;
        BrowserProgressBar.Visibility = Visibility.Visible;
        BeginFaviconNavigation(args.Uri);
        UpdateNavigationVisuals();
    }

    private void BrowserControl_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_isDisposed || _activeNavigationId != args.NavigationId) return;

        _activeNavigationId = null;
        UpdateNavigationVisuals();
        if (FindBar.Visibility == Visibility.Visible) QueueFind();
        ScheduleBackgroundSleep();
        BrowserProgressBar.Visibility = Visibility.Collapsed;

        try
        {
            BackBtn.IsEnabled = sender.CanGoBack;
            ForwardBtn.IsEnabled = sender.CanGoForward;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"NavigationCompleted: 更新导航按钮失败（{ex.GetType().Name}）");
        }

        CoreWebView2? core = _browserControl?.CoreWebView2;

        int status = args.HttpStatusCode;
        // Some servers send a usable HTML page with an HTTP error status, e.g.
        // a security verification page with 403. Let the site show its response.
        bool serverResponse = status >= 400 && status <= 599 &&
            (args.WebErrorStatus == CoreWebView2WebErrorStatus.Unknown ||
             args.WebErrorStatus == CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse ||
             args.WebErrorStatus == CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired);
        if (!args.IsSuccess && !serverResponse)
        {
            if (args.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted &&
                string.Equals(_downloadNavigationUrl, _lastRequestedUrl, StringComparison.Ordinal) && _downloadNavigationUrl != null) return;
            if (args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            _navigationFailed = true;
            string reason = args.WebErrorStatus.ToString();
            var failure = BrowserNavigationFailure.Describe(args.WebErrorStatus);
            ShowBrowserError(failure.Title, failure.Message);
            AppLog.Warn($"NavigationCompleted failed: {reason}");
            return;
        }

        string url = core?.Source ?? string.Empty;
        if (IsBlankDocumentUrl(url))
        {
            // about:blank / initial empty document: ignore for history and title.
            return;
        }

        if (UrlTextBox.FocusState == FocusState.Unfocused ||
            string.Equals(UrlTextBox.Text, _addressAtNavigationStart, StringComparison.Ordinal))
        {
            UrlTextBox.Text = url;
        }

        string title = core?.DocumentTitle ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(title) && !IsBlankDocumentUrl(title))
        {
            CurrentTitle = title;
            TitleChanged?.Invoke(title);
        }
        else
        {
            CurrentTitle = url;
            TitleChanged?.Invoke(url);
        }

        if (_historyRecorded) BrowserSessionHistory.UpdateTitle(title, url);
        else AddSessionHistory(title, url);
        UpdateBookmarkStarState();
        if (core != null && _favicon == null) CoreWebView2_FaviconChanged(core, EventArgs.Empty);

    }

    #endregion

    #region Small UI helpers

    private void ShowBrowserError(string title, string message)
    {
        BrowserErrorBar.Title = title;
        BrowserErrorBar.Message = message;
        UiMotion.Open(BrowserErrorBar);
        SetHubStatus(message);
    }

    private void UpdateEmptyNotices()
    {
        if (CookieEmptyNotice != null)
        {
            CookieEmptyNotice.Visibility = _filteredCookies.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (HistoryEmptyNotice != null)
        {
            HistoryEmptyNotice.Visibility = _sessionHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SetHubStatus(string message)
    {
        _hubStatus = message;
        if (HubStatusText != null) HubStatusText.Text = message;
    }

    public void FocusAddressBar()
    {
        try
        {
            UrlTextBox.Focus(FocusState.Programmatic);
            SelectAddressText();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"FocusAddressBar failed: {ex.GetType().Name}");
        }
    }

    #endregion
}
