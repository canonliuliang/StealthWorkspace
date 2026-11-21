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
    private static bool _privacyCoverActive;

    /// <summary>Number of tabs whose CoreWebView2 has been initialized in this process.
    /// Used by the UI smoke test to prove the browser kernel actually started.</summary>
    public static int InitializedTabCount => Volatile.Read(ref _initializedTabCount);
    internal static int LiveTabCount => LiveTabs.Count(w => w.TryGetTarget(out var tab) && !tab._isDisposed);

    /// <summary>Last browser kernel initialization error (type/code only, no paths/URLs).</summary>
    public static string LastInitializationError { get; private set; } = string.Empty;

    private readonly List<CookieDisplayItem> _allCookies = new();
    private readonly ObservableRangeCollection<CookieDisplayItem> _filteredCookies = new();
    private readonly BrowserTabPermissions _tabPermissions = new();
    private readonly SettingsService.BookmarkSession? _workspace = SettingsService.Instance.ActiveBookmarkSession;
    private bool CanAccessDownloads => !_isDisposed && BrowserDownloadPath.IsCurrentWorkspace(_workspace);
    private int _cookieLoadVersion;
    private string? _permissionPanelOrigin;
    private readonly ObservableCollection<HistorySessionItem> _sessionHistory = BrowserSessionHistory.Items;
    private readonly object _disposeSync = new();

    private Task? _disposeTask;
    private Task? _initializationTask;
    private TaskCompletionSource<bool>? _initializationCompletion;
    private CoreWebView2? _hookedCore;
    private Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2DownloadStartingEventArgs>? _downloadStartingHandler;
    private Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2PermissionRequestedEventArgs>? _permissionRequestedHandler;
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
    private bool _tabMutedByUser;
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
    private ulong? _activeNavigationId;
    private bool _documentReady;
    private bool _hadReadyDocumentBeforeNav;
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
    internal event Action? DocumentAddressChanged;
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
    internal UIElement NavigationChrome => TopNavBar;
    internal bool IsPageFullScreen => _isBrowserFullScreen;
    internal bool HasPrivacyUi => PrivacyContent != null;
    internal bool HasAuxiliaryUi => PrivacyContent != null || BookmarksContent != null || MoreActionsContent != null;
    internal bool IsSmokeSuspended => _hookedCore?.IsSuspended == true;
    internal bool IsSmokeBrowserAwake => _hookedCore is { IsSuspended: false };
    internal bool IsAudioMuted => _hookedCore?.IsMuted == true;
    internal bool HasPendingDownloadBadge => DownloadCountBadge.Visibility == Visibility.Visible;
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
        PrivacyMediaGuard.AppAudioMuteChanged += OnAppAudioMuteChanged;
    }

    #region Lifetime & Disposal

    private void BrowserTabControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isDisposed) return;

        BrowserDownloads.PendingCountChanged -= UpdateDownloadBadge;
        BrowserDownloads.PendingCountChanged += UpdateDownloadBadge;
        UpdateDownloadBadge();
        SubscribeSettings();
        AttachFaviconScale();
        LoadPermissionSettings();
        UpdateEmptyNotices();
        UpdateBookmarkStarState();
        RefreshStartBookmarks();
        QueueInitialAddressFocus();
    }

    internal void FocusAddressWhenLoaded() => _focusAddressOnLoad = true;

    private void QueueInitialAddressFocus()
    {
        if (!_focusAddressOnLoad) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_focusAddressOnLoad || !IsLoaded || _isDisposed || !_isSelected || MainWindow.Current?.IsPrivacyCoverVisible == true) return;
            if (StartSurface.Visibility == Visibility.Visible) UiMotion.Reveal(StartContent);
            FocusAddressBar();
        });
    }

    // A command made after opening a tab supersedes its deferred initial focus.
    // Otherwise that callback can move focus out of a newly opened flyout.
    private void CancelInitialAddressFocus() => _focusAddressOnLoad = false;

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
            // Tab switches are direct manipulation. Replaying a fade on the
            // navigation controls makes rapid switching flash and feel delayed.
        }
        else ScheduleBackgroundSleep();
    }

    private bool IsForegroundTab => _isSelected && _hostWindowVisible && !_privacyCoverActive;

    internal static void SetPrivacyCoverActive(bool active)
    {
        if (_privacyCoverActive == active) return;
        _privacyCoverActive = active;
        foreach (var tab in SnapshotLiveTabs())
        {
            tab.ApplyAppAudioMute();
            tab.UpdateBrowserVisibility();
            if (active) tab._backgroundMemoryReduced = false;
            else if (tab.IsForegroundTab || tab._isPlayingAudio || tab.HasRunningDownloads ||
                tab._isBrowserFullScreen || tab._activeNavigationId != null) tab.ResumeBrowser();
            tab.ScheduleBackgroundSleep();
        }
    }

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
        _isInitialized && !_suspending && !_backgroundMemoryReduced && (_privacyCoverActive ||
        (!_isPlayingAudio && !_isBrowserFullScreen && !HasRunningDownloads && (_activeNavigationId == null || _documentReady)));

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
        _backgroundTimer.Interval = retryDelay ?? (_privacyCoverActive ? TimeSpan.FromMilliseconds(1)
            : !_hostWindowVisible && _backgroundIdleDelay > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : _backgroundIdleDelay);
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
            if (_privacyCoverActive || _hookedCore is not { } core) return;
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
        SetHistorySearchActive(false);
        BrowserDownloads.PendingCountChanged -= UpdateDownloadBadge;
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
        BrowserSessionHistory.Clear();
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

        PrivacyMediaGuard.AppAudioMuteChanged -= OnAppAudioMuteChanged;
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
            if (_downloadsFlyout?.Content is DownloadListControl downloadList) downloadList.Detach();
            _downloadsFlyout?.Hide();
            _downloadsFlyout = null;
            BrowserDownloads.PendingCountChanged -= UpdateDownloadBadge;
            _displayedDownload = null;
            if (PrivacySections != null) PrivacySections.SelectionChanged -= PrivacySections_SelectionChanged;
            SetHistorySearchActive(false);
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
            DocumentAddressChanged = null;
            AudioPlayingChanged = null;
            NewTabRequested = null;
            PopupRequested = null;
            CloseRequested = null;
            DownloadsActivityChanged = null;
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
            // Apply the current policy before the new (or recreated) core can navigate.
            core.IsMuted = _privacyCoverActive || _tabMutedByUser || PrivacyMediaGuard.IsAppAudioMuted;

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

    private void ConfigureCoreWebView2(CoreWebView2 core)
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
            if (!CanAccessDownloads) return;
            string folder = AppPaths.IsolatedDownloadsRoot;
            Directory.CreateDirectory(folder);
            AppPaths.Hide(folder);
            core.Profile.DefaultDownloadFolderPath = folder;
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
            _permissionRequestedHandler = (_, args) => CoreWebView2_PermissionRequested(core, args);
            core.PermissionRequested += _permissionRequestedHandler;
            core.IsDocumentPlayingAudioChanged += CoreWebView2_IsDocumentPlayingAudioChanged;
            _downloadStartingHandler = (_, args) => CoreWebView2_DownloadStarting(core, args);
            core.DownloadStarting += _downloadStartingHandler;
            core.ContainsFullScreenElementChanged += CoreWebView2_ContainsFullScreenElementChanged;
            core.NewWindowRequested += CoreWebView2_NewWindowRequested;
            core.ContextMenuRequested += CoreWebView2_ContextMenuRequested;
            // WinRT may deliver a different managed wrapper for the same native
            // sender. Bind the callback to the core that owns this registration.
            _readerMessageHandler = (_, args) => ImageViewerMessageReceived(core, args);
            core.WebMessageReceived += _readerMessageHandler;
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
        _tabPermissions.SetDocument(null);
        ClearCookieRows();
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
            if (_permissionRequestedHandler != null) core.PermissionRequested -= _permissionRequestedHandler;
            _permissionRequestedHandler = null;
            core.IsDocumentPlayingAudioChanged -= CoreWebView2_IsDocumentPlayingAudioChanged;
            if (_downloadStartingHandler != null) core.DownloadStarting -= _downloadStartingHandler;
            _downloadStartingHandler = null;
            core.ContainsFullScreenElementChanged -= CoreWebView2_ContainsFullScreenElementChanged;
            core.NewWindowRequested -= CoreWebView2_NewWindowRequested;
            core.ContextMenuRequested -= CoreWebView2_ContextMenuRequested;
            if (_readerMessageHandler != null) core.WebMessageReceived -= _readerMessageHandler;
            _readerMessageHandler = null;
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
        _hadReadyDocumentBeforeNav = false;
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
        // WebView2 can finish native crash reporting several seconds after the CDP request times out.
        for (int i = 0; i < 150 && !BrowserErrorBar.IsOpen; i++) await Task.Delay(100);
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
                if (ok)
                {
                    var record = BrowserDownloads.Items.Single(item => item.FilePath == operation!.ResultFilePath);
                    var vault = VaultService.Instance;
                    var existing = vault.VisibleItems.Select(i => i.Id).ToHashSet();
                    await record.StoreInVaultAsync();
                    await record.StoreInVaultAsync();
                    var imported = vault.VisibleItems.Single(item => !existing.Contains(item.Id));
                    using (var reader = new StreamReader(vault.OpenDecryptedStream(imported)))
                        ok = !record.CanStore && !File.Exists(record.FilePath) && reader.ReadToEnd() == "synthetic download check";
                    await vault.DeleteItemAsync(imported);
                    SmokeTest.Report("browser: completed download stored once, decrypted bytes verified, original removed=" + ok);
                }
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

    private async void CoreWebView2_PermissionRequested(CoreWebView2 core, CoreWebView2PermissionRequestedEventArgs e)
    {
        // By default WebView2 may cache the decision and bypass our handler on
        // the next request, even after the user changes this switch.
        try { e.SavesInProfile = false; }
        catch (Exception ex)
        {
            e.State = CoreWebView2PermissionState.Deny;
            AppLog.Warn("WebView2 request-scoped permissions unavailable: " + ex.GetType().Name);
            return;
        }
        if (!CanAccessDownloads || _retainedForDownloads || !ReferenceEquals(core, _hookedCore))
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
            return;
        }
        _tabPermissions.SetDocument(core.Source);
        if (IsForegroundTab && IsLoaded && _tabPermissions.CanAskForFilePermission(e.PermissionKind, e.Uri))
        {
            var deferral = e.GetDeferral();
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
            try
            {
                if (await ConfirmFilePermissionAsync(core, e.PermissionKind, e.Uri))
                    e.State = CoreWebView2PermissionState.Allow;
            }
            catch (Exception ex) { AppLog.Warn("Confirm site permission: " + ex.GetType().Name); }
            finally
            {
                try { deferral.Complete(); }
                catch (Exception ex) { AppLog.Warn("Complete site permission: " + ex.GetType().Name); }
            }
            return;
        }
        if (!_tabPermissions.Blocks(e.PermissionKind, e.Uri, SettingsService.Instance.Data)) return;

        e.State = CoreWebView2PermissionState.Deny;
        e.Handled = true;
        if (_isDisposed || _retainedForDownloads) return;

        _blockedCounter++;
        UiMotion.SetVisible(ShieldBlockedBadge, true, MotionCue.Fade);
        ShieldBlockedCountText.Text = _blockedCounter.ToString(CultureInfo.InvariantCulture);
        SetHubStatus($"此标签页已阻止 {_blockedCounter} 次权限请求（{DescribePermissionKind(e.PermissionKind)}）");
    }

    private async Task<bool> ConfirmFilePermissionAsync(CoreWebView2 core, CoreWebView2PermissionKind kind, string requester)
    {
        if (!CanAccessDownloads || _retainedForDownloads || !_tabPermissions.CanAskForFilePermission(kind, requester)) return false;
        string origin = _tabPermissions.Origin!;
        ulong document = _imageDocumentId;
        bool files = kind == CoreWebView2PermissionKind.FileReadWrite;
        bool accepted = await ConfirmForCurrentTabAsync(core, files ? "允许本地文件读写？" : "允许连续下载多个文件？",
            files ? $"{origin} 请求读写你通过文件选择器选定的文件或文件夹。" : $"{origin} 请求在本次操作中连续下载多个文件。",
            "允许本次请求");
        return accepted && CanAccessDownloads && !_retainedForDownloads && document == _imageDocumentId && origin == _tabPermissions.Origin;
    }

    private static string DescribePermissionKind(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Camera => "摄像头",
        CoreWebView2PermissionKind.Microphone => "麦克风",
        CoreWebView2PermissionKind.Geolocation => "位置信息",
        CoreWebView2PermissionKind.Notifications => "通知",
        CoreWebView2PermissionKind.ClipboardRead => "读取剪贴板",
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => "多文件连续下载",
        CoreWebView2PermissionKind.FileReadWrite => "本地文件读写",
        CoreWebView2PermissionKind.Autoplay => "自动播放",
        CoreWebView2PermissionKind.LocalFonts => "本地字体枚举",
        CoreWebView2PermissionKind.MidiSystemExclusiveMessages => "MIDI 设备",
        CoreWebView2PermissionKind.WindowManagement => "多屏幕窗口管理",
        _ => kind.ToString()
    };

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
            string? document = PermissionScope.SelectedIndex == 0 ? _hookedCore?.Source : null;
            CameraBlockToggle.IsOn = _tabPermissions.Blocks(CoreWebView2PermissionKind.Camera, document, cfg);
            MicBlockToggle.IsOn = _tabPermissions.Blocks(CoreWebView2PermissionKind.Microphone, document, cfg);
            GeoBlockToggle.IsOn = _tabPermissions.Blocks(CoreWebView2PermissionKind.Geolocation, document, cfg);
            NotifyBlockToggle.IsOn = _tabPermissions.Blocks(CoreWebView2PermissionKind.Notifications, document, cfg);
        }
        finally
        {
            _isUpdatingToggles = false;
        }
    }

    private void PermissionScope_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadPermissionSettings();

    private void PermissionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingToggles || _isDisposed) return;
        if (CameraBlockToggle == null || MicBlockToggle == null || GeoBlockToggle == null || NotifyBlockToggle == null)
        {
            return;
        }

        AppSettingsData cfg = SettingsService.Instance.Data;
        var toggle = (ToggleSwitch)sender;
        CoreWebView2PermissionKind kind;
        if (toggle == CameraBlockToggle) kind = CoreWebView2PermissionKind.Camera;
        else if (toggle == MicBlockToggle) kind = CoreWebView2PermissionKind.Microphone;
        else if (toggle == GeoBlockToggle) kind = CoreWebView2PermissionKind.Geolocation;
        else if (toggle == NotifyBlockToggle) kind = CoreWebView2PermissionKind.Notifications;
        else return;
        if (PermissionScope.SelectedIndex == 0)
        {
            if (_permissionPanelOrigin == null || _permissionPanelOrigin != _tabPermissions.Origin) return;
            _tabPermissions.SetSiteRule(kind, toggle.IsOn, cfg);
            return;
        }
        if (PermissionScope.SelectedIndex != 1) return;
        switch (kind)
        {
            case CoreWebView2PermissionKind.Camera: cfg.BlockCamera = toggle.IsOn; break;
            case CoreWebView2PermissionKind.Microphone: cfg.BlockMicrophone = toggle.IsOn; break;
            case CoreWebView2PermissionKind.Geolocation: cfg.BlockGeolocation = toggle.IsOn; break;
            case CoreWebView2PermissionKind.Notifications: cfg.BlockNotifications = toggle.IsOn; break;
        }
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
        if (dispatcher == null) return;
        if (dispatcher.HasThreadAccess)
        {
            LoadPermissionSettings();
            UpdateBookmarkStarState();
            RefreshStartBookmarks();
            RefreshBookmarkList();
            return;
        }

        dispatcher.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            LoadPermissionSettings();
            UpdateBookmarkStarState();
            RefreshStartBookmarks();
            RefreshBookmarkList();
        });
    }

    #endregion

    #region Downloads Interception

    private void CoreWebView2_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        if (!CanAccessDownloads || _retainedForDownloads || !ReferenceEquals(sender, _hookedCore))
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
            if (SameDownloadUrl(_downloadNavigationUrl, _lastRequestedUrl))
            {
                BrowserErrorBar.IsOpen = false;
                _navigationFailed = false;
                if (_activeNavigationId == null) RestoreDocumentAfterDownload();
            }

            string displayName = Path.GetFileName(targetPath);
            ShowDownloadToast("\uE896", $"正在下载：{displayName}", "文件保存在应用下载文件夹，未自动加密。");
            SetHubStatus("下载保存在应用文件夹，未自动加密");
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
                ShowDownloadToast("\uE7BA", "下载已阻止", "无法创建应用下载文件夹，下载未开始。");
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
                    ShowDownloadToast("\uE73E", "下载完成", record?.FileName ?? "文件已保存在应用下载文件夹。", record);
                    AppLog.Info("Download: 已下载到应用隐藏隔离目录");
                    break;

                case CoreWebView2DownloadState.Interrupted:
                    // Current runtimes report UserPaused; older versions may use
                    // UserCanceled. Keep resumable operations attached in both cases.
                    if (!operation.CanResume) DetachDownloadStateHandler(operation);
                    else ScheduleBackgroundSleep();
                    bool paused = operation.CanResume && Models.BrowserDownloadItem.IsUserPause(operation.InterruptReason);
                    bool cancelled = !operation.CanResume && operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled;
                    string title = cancelled ? "下载已取消" : paused ? "下载已暂停" : "下载已中断";
                    ShowDownloadToast(cancelled || paused ? "\uE769" : "\uE7BA", title,
                        cancelled ? "已停止接收此文件" : operation.CanResume ? "可以在下载列表中继续" : "下载未完成，可重新下载");
                    SetHubStatus(title);
                    if (!paused && !cancelled) AppLog.Warn($"Download interrupted: {operation.InterruptReason}");
                    break;

                case CoreWebView2DownloadState.InProgress:
                    ResumeBrowser();
                    ScheduleBackgroundSleep();
                    _displayedDownload = operation;
                    _downloadProgressUpdatedAt = 0;
                    DownloadVaultActions.Visibility = Visibility.Collapsed;
                    DownloadVaultActions.DataContext = null;
                    DownloadToastIcon.Glyph = "\uE896";
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

    private void UpdateDownloadBadge()
    {
        int count = BrowserDownloads.PendingCount;
        DownloadCountText.Text = count.ToString(CultureInfo.CurrentCulture);
        DownloadCountBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DownloadsBtn, count > 0 ? $"下载，{count} 个未完成" : "下载");
    }

    private async void StoreDownload_Click(object sender, RoutedEventArgs args)
    {
        if (CanAccessDownloads && DownloadVaultActions.DataContext is BrowserDownloadItem item &&
            BrowserDownloads.Items.Contains(item)) await item.StoreInVaultAsync();
    }

    private void ShowDownloadToast(string glyph, string title, string description, BrowserDownloadItem? completed = null)
    {
        DownloadVaultActions.DataContext = completed;
        DownloadVaultActions.Visibility = completed != null ? Visibility.Visible : Visibility.Collapsed;
        bool changedState = DownloadToast.Visibility == Visibility.Visible && DownloadToastIcon.Glyph != glyph;
        DownloadToastIcon.Glyph = glyph;
        DownloadToastTitle.Text = title;
        DownloadToastDesc.Text = description;
        UiMotion.SetOverlayVisible(DownloadToast, true, MotionCue.FromBelow);
        if (changedState && IsForegroundTab) UiMotion.Reveal(DownloadToastIcon, MotionCue.Fade);
    }

    private void DismissDownloadToast_Click(object sender, RoutedEventArgs e)
    {
        UiMotion.SetOverlayVisible(DownloadToast, false, MotionCue.FromBelow);
    }

    internal void OpenDownloadsFromShortcut() => ShowDownloads_Click(this, new RoutedEventArgs());

    #endregion

    #region Shortcuts, Zoom & Find

    private void BrowserTabControl_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isDisposed) return;
        if (_nativeFindDispatching && (e.Key is VirtualKey.F or VirtualKey.F3)) return;

        bool isCtrl = IsKeyPressed(VirtualKey.Control), isAlt = IsKeyPressed(VirtualKey.Menu), shift = IsKeyPressed(VirtualKey.Shift);
        // AltGr is reported as Ctrl+Alt on Windows. Keep these keystrokes available to text editors.
        if (isCtrl && isAlt || IsKeyPressed(VirtualKey.LeftWindows) || IsKeyPressed(VirtualKey.RightWindows)) return;
        bool plainCtrl = isCtrl && !shift;
        if (plainCtrl && e.Key == VirtualKey.H) { e.Handled = true; OpenSessionHistory(); return; }

        if (plainCtrl && e.Key == VirtualKey.D)
        {
            BookmarkStarBtn_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && FindBar.Visibility == Visibility.Visible)
        {
            CloseFindBar();
            e.Handled = true;
        }
        else if (!isCtrl && !isAlt && e.Key == VirtualKey.F3)
        {
            FindFromShortcut(advance: true, backwards: shift);
            e.Handled = true;
        }
        else if (!isAlt && (e.Key == VirtualKey.F5 || isCtrl && e.Key == VirtualKey.R))
        {
            if (shift || isCtrl && e.Key == VirtualKey.F5) _ = ReloadIgnoringCacheAsync();
            else ReloadCurrentPage();
            e.Handled = true;
        }
        else if (isAlt && !shift && e.Key == VirtualKey.Left)
        {
            NavigateBack();
            e.Handled = true;
        }
        else if (isAlt && !shift && e.Key == VirtualKey.Right)
        {
            NavigateForward();
            e.Handled = true;
        }
        else if (plainCtrl && e.Key == VirtualKey.F)
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

    private void BookmarksFlyout_Opening(object? sender, object e)
    {
        if (_isDisposed) return;
        CancelInitialAddressFocus();
        if (BookmarksContent == null) FindName(nameof(BookmarksContent));
        _bookmarkFilter = new();
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

    #endregion

    #region Audio Indicator & Mute

    internal void ToggleAudioMute() => AudioMuteBtn_Click(this, new RoutedEventArgs());

    private void OnAppAudioMuteChanged(bool _)
    {
        if (DispatcherQueue.HasThreadAccess) ApplyAppAudioMute();
        else DispatcherQueue.TryEnqueue(ApplyAppAudioMute);
    }

    private void ApplyAppAudioMute()
    {
        if (_isDisposed || _hookedCore is not { } core) return;
        try
        {
            core.IsMuted = _privacyCoverActive || _tabMutedByUser || PrivacyMediaGuard.IsAppAudioMuted;
            UpdateAudioIcon();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"更新浏览器静音状态失败: {ex.GetType().Name}");
        }
    }

    private void UpdateAudioIcon()
    {
        CoreWebView2? core = _browserControl?.CoreWebView2;
        bool isMuted = core != null && core.IsMuted;
        if (AudioMuteBtn != null)
        {
            AudioMuteBtn.IsEnabled = core != null && !_privacyCoverActive && !PrivacyMediaGuard.IsAppAudioMuted;
            AudioMuteBtn.Content = isMuted ? "取消静音" : "静音标签页";
        }
        NavigationVisualChanged?.Invoke();
    }

    private void AudioMuteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isDisposed || _privacyCoverActive || PrivacyMediaGuard.IsAppAudioMuted) return;

        CoreWebView2? core = _browserControl?.CoreWebView2;
        if (core == null) return;

        try
        {
            bool mute = !core.IsMuted;
            core.IsMuted = mute;
            _tabMutedByUser = mute;
            UpdateAudioIcon();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"AudioMuteBtn_Click failed: {ex.GetType().Name}");
        }
    }

    #endregion

    #region Privacy Hub Flyout

    private int _privacySectionIndex;

    private void PrivacySections_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not { } selection) return;
        int index = sender.Items.IndexOf(selection);
        if (index < 0 || index == _privacySectionIndex) return;
        bool fromRight = (index > _privacySectionIndex) == (sender.FlowDirection == FlowDirection.LeftToRight);
        PrivacyPanels.Children[_privacySectionIndex].Visibility = Visibility.Collapsed;
        _privacySectionIndex = index;
        UiMotion.SetVisible((FrameworkElement)PrivacyPanels.Children[index], true, fromRight ? MotionCue.FromRight : MotionCue.FromLeft);
        if (selection == CookiesSection) _ = RefreshCookiesAsync();
        else ClearCookieRows();
        SetHistorySearchActive(selection == HistorySection && PrivacyFlyout.IsOpen);
    }

    private void CloseFlyout_Click(object sender, RoutedEventArgs e)
    {
        PrivacyFlyout.Hide();
    }

    private void PrivacyFlyout_Closed(object? sender, object e)
    {
        _focusHistoryOnOpen = false;
        _permissionPanelOrigin = null;
        ClearCookieRows();
        SetHistorySearchActive(false);
    }

    private void PrivacyFlyout_Opening(object? sender, object e)
    {
        CancelInitialAddressFocus();
        EnsurePrivacyContent();
        string currentUrl = GetCurrentDocumentUrl();
        _tabPermissions.SetDocument(currentUrl);
        _permissionPanelOrigin = _tabPermissions.Origin;
        CurrentSiteScope.IsEnabled = _tabPermissions.Origin != null;
        ClearSiteStorageBtn.IsEnabled = CurrentSiteScope.IsEnabled;
        PermissionScope.SelectedIndex = CurrentSiteScope.IsEnabled ? 0 : 1;
        LoadPermissionSettings();
        FlyoutDomainText.Text = DescribeHost(currentUrl);
        SessionStateText.Text = Uri.TryCreate(currentUrl, UriKind.Absolute, out var site) && site.Scheme is "http" or "https"
            ? site.Scheme == "https" ? "HTTPS · 临时会话" : "HTTP · 连接未加密"
            : _isInitialized ? "临时会话已启动" : "尚未打开网页";
        UpdateEmptyNotices();

        if (PanelCookie.Visibility == Visibility.Visible)
        {
            _ = RefreshCookiesAsync();
        }
        SetHistorySearchActive(PanelHistory.Visibility == Visibility.Visible);
    }

    private void EnsurePrivacyContent()
    {
        if (PrivacyContent != null) return;
        // Group the whole rarely-used panel behind one placeholder. The permission
        // policy continues to read SettingsService even before this UI exists.
        _isUpdatingToggles = true;
        try { FindName(nameof(PrivacyContent)); }
        finally { _isUpdatingToggles = false; }
        PrivacySections.SelectedItem = PermissionsSection;
        PrivacySections.SelectionChanged += PrivacySections_SelectionChanged;
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
        bool menusDeferred = BookmarksContent == null && MoreActionsContent == null;
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
        // Exercise real flyout opening so deferred controls are instantiated by
        // the same event path as a click. A second opening must reuse the tree.
        // ShowAt can return before Opening is dispatched, especially under x64
        // emulation. Observe the actual lifecycle instead of sampling that return.
        async Task<(bool Loaded, bool FocusStable)> CheckMenuAsync(string name, Flyout flyout,
            FrameworkElement anchor, Func<bool> isContentLoaded)
        {
            int opening = 0, opened = 0, closed = 0;
            bool auditHide = false;
            var hidden = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            string FocusDescription() => XamlRoot is { } root && FocusManager.GetFocusedElement(root) is FrameworkElement focused
                ? focused.GetType().Name + ":" + focused.Name : "none";
            void Opening(object? sender, object args)
            {
                opening++;
                SmokeTest.Report($"browser menu {name}: Opening; addressPending={_focusAddressOnLoad}; focus={FocusDescription()}");
            }
            void Opened(object? sender, object args)
            {
                opened++;
                SmokeTest.Report($"browser menu {name}: Opened; addressPending={_focusAddressOnLoad}; focus={FocusDescription()}");
            }
            void Closed(object? sender, object args)
            {
                if (auditHide) hidden.TrySetResult(true);
                else
                {
                    closed++;
                    SmokeTest.Report($"browser menu {name}: Closed before audit hide; addressPending={_focusAddressOnLoad}; focus={FocusDescription()}");
                }
            }
            flyout.Opening += Opening;
            flyout.Opened += Opened;
            flyout.Closed += Closed;
            try
            {
                FocusAddressWhenLoaded();
                QueueInitialAddressFocus();
                flyout.ShowAt(anchor);
                var dispatched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => dispatched.TrySetResult(true));
                for (int i = 0; i < 40 && (opening == 0 || opened == 0 || !isContentLoaded() || !dispatched.Task.IsCompleted); i++)
                    await Task.Delay(25);
                bool loaded = isContentLoaded();
                bool focusStable = opening > 0 && opened > 0 && closed == 0 && flyout.IsOpen &&
                    dispatched.Task.IsCompleted && !_focusAddressOnLoad;
                SmokeTest.Report($"browser menu {name}: opening={opening}; opened={opened}; closed={closed}; contentLoaded={loaded}; isOpen={flyout.IsOpen}; initialFocusCancelled={!_focusAddressOnLoad}; focusCallbackDrained={dispatched.Task.IsCompleted}; focus={FocusDescription()}");
                return (loaded, focusStable);
            }
            finally
            {
                auditHide = true;
                bool wasOpen = flyout.IsOpen;
                try
                {
                    flyout.Hide();
                    // Finish the real closing transition before another menu is
                    // opened; no fixed animation delay is assumed by this test.
                    if (wasOpen) await hidden.Task.WaitAsync(TimeSpan.FromSeconds(1));
                }
                finally
                {
                    flyout.Opening -= Opening;
                    flyout.Opened -= Opened;
                    flyout.Closed -= Closed;
                }
            }
        }
        async Task<(bool Loaded, bool FocusStable)> CheckBookmarkMenuAsync(string name)
        {
            // Prove that XAML projects a nonempty immutable source, including
            // its first row. The smoke fixture never needs to persist this item.
            var original = cfg.Bookmarks;
            var marker = new BookmarkItem { Title = "Fixture bookmark", Url = "https://bookmarks.invalid/check" };
            cfg.Bookmarks = original.Insert(0, marker);
            try
            {
                return await CheckMenuAsync(name, BookmarksFlyout, FavoritesBtn, () => BookmarksContent?.IsLoaded == true &&
                    BookmarksListView.Items.Count == cfg.Bookmarks.Count && ReferenceEquals(BookmarksListView.Items[0], marker));
            }
            finally { cfg.Bookmarks = original; }
        }
        var bookmarkProbe = await CheckBookmarkMenuAsync("bookmarks");
        var bookmarks = BookmarksContent;
        bool bookmarksLoaded = bookmarkProbe.Loaded && BookmarksListView.ItemsSource == null && _bookmarkFilter == null;
        var actionsProbe = await CheckMenuAsync("actions", MoreActionsFlyout, BrowserMenuBtn, () => MoreActionsContent?.IsLoaded == true);
        var actions = MoreActionsContent;
        bool actionsLoaded = actionsProbe.Loaded && AudioMuteBtn != null && ZoomResetBtn != null;
        var bookmarkReuse = await CheckBookmarkMenuAsync("bookmarks-reuse");
        bool reusedBookmarks = bookmarkReuse.Loaded && ReferenceEquals(bookmarks, BookmarksContent);
        var actionsReuse = await CheckMenuAsync("actions-reuse", MoreActionsFlyout, BrowserMenuBtn, () => MoreActionsContent?.IsLoaded == true);
        bool reusedActions = actionsReuse.Loaded && ReferenceEquals(actions, MoreActionsContent);
        bool menusOk = menusDeferred && bookmarksLoaded && actionsLoaded && reusedBookmarks && reusedActions;
        SmokeTest.Report("browser: deferred bookmark/actions flyouts open, bind and reuse controls=" + menusOk);
        bool focusOk = bookmarkProbe.FocusStable && actionsProbe.FocusStable && bookmarkReuse.FocusStable && actionsReuse.FocusStable;
        SmokeTest.Report("browser: bookmark/actions commands cancel deferred address focus=" + focusOk);
        return ok && menusOk && focusOk;
    }

    #endregion

    #region Cookie Management

    private void ClearCookieRows()
    {
        _cookieLoadVersion++;
        _allCookies.Clear();
        if (_filteredCookies.Count > 0)
        {
            _filteredCookies.Clear();
            UpdateEmptyNotices();
        }
    }

    private async Task RefreshCookiesAsync()
    {
        if (_isDisposed) return;

        CoreWebView2? core = _hookedCore;
        if (core == null || !TryCreateWebUri(core.Source, out var currentUri) || currentUri == null)
        {
            ClearCookieRows();
            SetHubStatus("当前没有可查看 Cookie 的网页");
            return;
        }

        int version = ++_cookieLoadVersion;
        try
        {
            // An empty URI would return cookies from every tab's origin.
            var cookies = await core.CookieManager.GetCookiesAsync(currentUri.AbsoluteUri);
            if (_isDisposed || version != _cookieLoadVersion || !ReferenceEquals(core, _hookedCore)) return;

            _allCookies.Clear();
            foreach (CoreWebView2Cookie cookie in cookies)
            {
                _allCookies.Add(new CookieDisplayItem
                {
                    Name = cookie.Name,
                    Domain = cookie.Domain,
                    Path = cookie.Path,
                    IsSecure = cookie.IsSecure,
                    IsHttpOnly = cookie.IsHttpOnly,
                });
            }

            FilterCookies();
            SetHubStatus($"当前网页匹配 {_allCookies.Count} 个 Cookie");
        }
        catch (Exception ex)
        {
            if (_isDisposed || version != _cookieLoadVersion) return;
            ClearCookieRows();
            AppLog.Error("RefreshCookiesAsync failed", ex);
            SetHubStatus("读取 Cookie 失败（详情见诊断日志）");
        }
    }

    private void FilterCookies()
    {
        string query = CookieSearchBox?.Text?.Trim() ?? string.Empty;
        _filteredCookies.Reconcile(query.Length == 0 ? _allCookies : _allCookies.Where(cookie =>
            cookie.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            cookie.Domain.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray());

        UpdateEmptyNotices();
    }

    private void CookieSearchBox_TextChanged(object sender, TextChangedEventArgs e) => FilterCookies();

    private async void RefreshCookies_Click(object sender, RoutedEventArgs e) => await RefreshCookiesAsync();

    private async void DeleteSingleCookie_Click(object sender, RoutedEventArgs e)
    {
        if (_isDisposed) return;

        CoreWebView2? core = _hookedCore;
        if (core == null) return;

        if ((sender as FrameworkElement)?.Tag is not CookieDisplayItem cookieItem || !_allCookies.Contains(cookieItem)) return;

        int version = _cookieLoadVersion;
        try
        {
            await BrowserSiteData.DeleteMatchingCookiesAsync(core,
                cookie => cookie.Name == cookieItem.Name && cookie.Domain == cookieItem.Domain && cookie.Path == cookieItem.Path,
                () => !_isDisposed && ReferenceEquals(core, _hookedCore) && version == _cookieLoadVersion);
            if (_isDisposed || !ReferenceEquals(core, _hookedCore) || version != _cookieLoadVersion) return;
            _allCookies.Remove(cookieItem);
            _filteredCookies.Remove(cookieItem);
            UpdateEmptyNotices();
            SetHubStatus("已删除所选 Cookie");
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
        int version = _cookieLoadVersion;
        try
        {
            int count = await BrowserSiteData.DeleteCookiesForHostAsync(core, host,
                () => !_isDisposed && ReferenceEquals(_hookedCore, core));
            if (_isDisposed || !ReferenceEquals(_hookedCore, core) || version != _cookieLoadVersion) return;
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
            if (!await ConfirmSessionDataClearAsync(core, "清除所有网站的 Cookie？",
                "本次会话的所有标签页都会受影响。已登录的网站可能要求你重新登录。")) return;
            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.Cookies);
            if (_isDisposed || core != _hookedCore) return;
            ClearCookieRows();
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

    private async void ClearSiteStorage_Click(object sender, RoutedEventArgs e)
    {
        if (_hookedCore is not { } core || _permissionPanelOrigin is not { } origin ||
            origin != _tabPermissions.Origin || !TryCreateWebUri(core.Source, out var page) || page == null) return;
        ulong documentId = _imageDocumentId;
        try
        {
            string address = page.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
            if (!await ConfirmSessionDataClearAsync(core, "清除本站的离线数据？",
                $"{address}\n本次会话中，此来源的本地存储、IndexedDB 数据库和离线缓存都将被清除，未同步草稿可能丢失。Cookie 会保留。") ||
                documentId != _imageDocumentId || origin != _tabPermissions.Origin) return;
            await BrowserSiteData.ClearOfflineDataForOriginAsync(core, page);
            if (!_isDisposed && ReferenceEquals(core, _hookedCore) && documentId == _imageDocumentId)
                SetHubStatus("已清除本站的本地存储、IndexedDB 和离线缓存");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Clear site offline data: " + ex.GetType().Name);
            if (!_isDisposed && documentId == _imageDocumentId) SetHubStatus("未能清除本站的离线数据，请重试");
        }
    }

    private Task<bool> ConfirmSessionDataClearAsync(CoreWebView2 core, string title, string detail) =>
        ConfirmForCurrentTabAsync(core, title, detail, "确认清除");

    private async Task<bool> ConfirmForCurrentTabAsync(CoreWebView2 core, string title, string detail, string primary)
    {
        if (!IsLoaded || !IsForegroundTab || _isDisposed || core != _hookedCore) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = title, Content = detail,
            PrimaryButtonText = primary, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        var choice = await WorkspaceDialogs.ShowAsync(dialog);
        return choice == ContentDialogResult.Primary && !_isDisposed && IsLoaded && IsForegroundTab && core == _hookedCore;
    }

    private async void ClearAllStorage_Click(object sender, RoutedEventArgs e)
    {
        if (_isDisposed) return;

        CoreWebView2? core = _browserControl?.CoreWebView2;
        if (core == null) return;

        try
        {
            if (!await ConfirmSessionDataClearAsync(core, "清除所有网站的本地存储？",
                "本次会话中各网站保存的离线文件、数据库和未同步草稿都可能丢失。Cookie 会保留。")) return;
            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllDomStorage);
            if (_isDisposed || core != _hookedCore) return;

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
            await BrowserSiteData.ClearHttpCacheAsync(core);
            if (_isDisposed || core != _hookedCore) return;
            SetHubStatus("已清除 HTTP 磁盘缓存");
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
            BrowserSessionHistory.Forget(item);
            UpdateEmptyNotices();
        }
    }

    private void ClearSessionHistory_Click(object sender, RoutedEventArgs e)
    {
        BrowserSessionHistory.Clear();
        UpdateEmptyNotices();
        SetHubStatus("临时会话历史和最近关闭的标签记录已清空");
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
        var bookmarks = SettingsService.Instance.CurrentBookmarks.Where(item => !item.ContainsLoginInfo).Take(12).ToArray();
        // Loaded is raised again when returning to a tab. Retain the existing
        // controls and focus if the displayed bookmarks have not changed.
        if (StartBookmarks.ItemsSource is IReadOnlyList<BookmarkItem> existing && existing.SequenceEqual(bookmarks)) return;
        StartBookmarks.ItemsSource = bookmarks;
        StartBookmarksEmpty.Visibility = bookmarks.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MoreActionsFlyout_Opening(object? sender, object e)
    {
        if (_isDisposed) return;
        CancelInitialAddressFocus();
        if (MoreActionsContent == null) FindName(nameof(MoreActionsContent));
        UpdateAudioIcon();
        PageImagesBtn.IsEnabled = _isInitialized && _hookedCore != null && _documentReady;
        foreach (var control in ZoomMenuRow.Children.OfType<Control>()) control.IsEnabled = _isInitialized && _hookedCore != null;
        _ = RefreshNativeZoomAsync();
    }

    private void ShowFavorites_Click(object sender, RoutedEventArgs e) => BookmarksFlyout.ShowAt(FavoritesBtn);

    private void ShowHistory_Click(object sender, RoutedEventArgs e)
    {
        _historyAfterMenuClose = true;
        MoreActionsFlyout.Hide();
    }

    internal void OpenSessionHistory()
    {
        if (_isDisposed || !IsLoaded || !IsForegroundTab || !VaultService.Instance.IsUnlocked) return;
        EnsurePrivacyContent();
        PrivacySections.SelectedItem = HistorySection;
        if (PrivacyFlyout.IsOpen) HistorySearchBox.Focus(FocusState.Keyboard);
        else
        {
            _focusHistoryOnOpen = true;
            PrivacyFlyout.ShowAt(SiteShieldBtn);
        }
    }

    private async void OpenDownloadsFolder_Click(object sender, RoutedEventArgs e)
    {
        MoreActionsFlyout.Hide();
        if (!CanAccessDownloads) return;
        try
        {
            string directory = AppPaths.IsolatedDownloadsRoot;
            Directory.CreateDirectory(directory);
            AppPaths.Hide(directory);
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(directory);
            if (!CanAccessDownloads) return;
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
        if (!CanAccessDownloads) return;
        CancelInitialAddressFocus();
        try
        {
            var flyout = EnsureDownloadsFlyout();
            if (!flyout.IsOpen) flyout.ShowAt(DownloadsBtn);
            UiMotion.SetVisible(DownloadToast, false);
        }
        catch (Exception ex) { AppLog.Warn("Download dialog failed: " + ex.GetType().Name); ShowBrowserError("无法显示下载记录", "可以从更多菜单打开下载文件夹。"); }
    }

    private Flyout EnsureDownloadsFlyout() => _downloadsFlyout ??= new Flyout
    {
        Content = new DownloadListControl(),
        Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
    };

    private void CoreWebView2_DocumentTitleChanged(CoreWebView2 sender, object args)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(sender.DocumentTitle)) return;
        CurrentTitle = DisplayAddress(sender.DocumentTitle);
        TitleChanged?.Invoke(CurrentTitle);
        BrowserSessionHistory.UpdateTitle(CurrentTitle, sender.Source);
    }

    private void CoreWebView2_HistoryChanged(CoreWebView2 sender, object args)
    {
        if (_isDisposed) return;
        BackBtn.IsEnabled = sender.CanGoBack;
        ForwardBtn.IsEnabled = sender.CanGoForward;
        if (!IsEditingAddress) UrlTextBox.Text = DisplayAddress(sender.Source);
    }

    private void CoreWebView2_SourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args)
    {
        if (_isDisposed) return;
        if (args.IsNewDocument) _hadReadyDocumentBeforeNav = false;
        DocumentAddressChanged?.Invoke();
        _tabPermissions.SetDocument(sender.Source);
        UpdatePopupButton();
        if (PrivacyFlyout.IsOpen) PrivacyFlyout.Hide();
        ClearCookieRows();
        // Commit the real address on redirects/SPA navigation without waiting for
        // slow images or ads. Preserve an address the user has started editing.
        if (!IsEditingAddress) UrlTextBox.Text = DisplayAddress(sender.Source);
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
        for (int i = 0; i < 60 && (_faviconSourceUri != dataUri || (_favicon?.PixelWidth ?? 0) < FaviconPixels); i++) await Task.Delay(50);
        bool sharpIcon = _faviconSourceUri == dataUri && _faviconSourcePixels >= FaviconPixels && (_favicon?.PixelWidth ?? 0) >= FaviconPixels;
        SmokeTest.Report($"browser: favicon original pixels={_faviconSourcePixels}, rendered pixels={_favicon?.PixelWidth}, DPI target={FaviconPixels}, sharp={sharpIcon}");
        bool canvas = await core.ExecuteScriptAsync("(()=>{try{document.createElement('canvas').getContext('2d').getImageData(0,0,1,1);return true}catch{return false}})()") == "true";
        SmokeTest.Report("browser: standard canvas readback available=" + canvas);
        ToggleAudioMute();
        bool muted = core.IsMuted;
        try { PrivacyMediaGuard.SetAppAudioMuted(true); }
        finally { PrivacyMediaGuard.SetAppAudioMuted(false); }
        bool keptUserMute = core.IsMuted;
        ToggleAudioMute();
        SmokeTest.Report($"browser: dynamic title + in-memory favicon={visual}; mute toggles={muted && !core.IsMuted}; user mute restored={keptUserMute}");
        return canvas && sharpIcon && visual && muted && keptUserMute && !core.IsMuted;
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
            UrlTextBox.Text = DisplayAddress(CurrentUrl);
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

        if (!IsEditingAddress) UrlTextBox.Text = uri.AbsoluteUri;

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

    // A data: URL contains the document itself. Its scheme identifies the active
    // document without copying a potentially large body into native UI controls.
    internal static string DisplayAddress(string url) =>
        url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? "data:" : url;

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
        _imageDocumentId = args.NavigationId;
        _contextImageDocument = _contextImageSource = _readerTicket = null;
        CancelResponseProbe();
        StopFind();
        HideZoomPanel();

        if (_activeNavigationId != args.NavigationId)
        {
            _blockedPopupUrls.Clear();
            _blockedPopupCount = 0;
            UpdatePopupButton();
            _activeNavigationId = args.NavigationId;
        }
        if (_privacyCoverActive) ScheduleBackgroundSleep();
        else _backgroundTimer?.Stop();
        // Redirects and a second navigation can start before the previous one commits.
        // SourceChanged clears this flag when WebView2 actually replaces the old page.
        _hadReadyDocumentBeforeNav |= _documentReady && !IsBlankDocumentUrl(_hookedCore?.Source);
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

    private static bool SameDownloadUrl(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        if (Uri.TryCreate(a, UriKind.Absolute, out Uri? ua) && Uri.TryCreate(b, UriKind.Absolute, out Uri? ub))
        {
            // WebView2 may escape literal brackets when handing off to its
            // downloader. Keep encoded separators such as %2F and %26 distinct.
            static string RequestUrl(Uri uri) => uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)
                .Replace("%5B", "[", StringComparison.OrdinalIgnoreCase).Replace("%5D", "]", StringComparison.OrdinalIgnoreCase);
            return RequestUrl(ua) == RequestUrl(ub);
        }
        return false;
    }

    private bool RestoreDocumentAfterDownload()
    {
        if (!_hadReadyDocumentBeforeNav || _hookedCore is not { } core || IsBlankDocumentUrl(core.Source)) return false;
        _documentReady = true;
        if (_favicon == null) CoreWebView2_FaviconChanged(core, EventArgs.Empty);
        NavigationVisualChanged?.Invoke();
        return true;
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
            bool downloadAborted = args.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted &&
                _downloadNavigationUrl != null && SameDownloadUrl(_downloadNavigationUrl, _lastRequestedUrl);
            if (downloadAborted || args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            {
                if (!RestoreDocumentAfterDownload() && args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled && IsBlankDocumentUrl(core?.Source))
                {
                    // Initial load stopped before commit: mark so Reload retries _lastRequestedUrl instead of about:blank.
                    _navigationFailed = true;
                }
                return;
            }
            _navigationFailed = true;
            string reason = args.WebErrorStatus.ToString();
            var failure = BrowserNavigationFailure.Describe(args.WebErrorStatus);
            ShowBrowserError(failure.Title, failure.Message);
            AppLog.Warn($"NavigationCompleted failed: {reason}");
            return;
        }

        _documentReady = true;
        _hadReadyDocumentBeforeNav = false;
        string url = core?.Source ?? string.Empty;
        if (IsBlankDocumentUrl(url))
        {
            if (!IsEditingAddress)
                UrlTextBox.Text = DisplayAddress(url);
            string? documentTitle = core?.DocumentTitle;
            CurrentTitle = !string.IsNullOrWhiteSpace(documentTitle) && !IsBlankDocumentUrl(documentTitle)
                ? documentTitle : DisplayAddress(url) switch { "about:blank" or "" => "空白页", "data:" => "内嵌文档", var address => address };
            TitleChanged?.Invoke(CurrentTitle);
            UpdateBookmarkStarState();
            return;
        }

        if (!IsEditingAddress)
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
        BrowserErrorBar.Severity = InfoBarSeverity.Error;
        BrowserErrorBar.ActionButton.Visibility = Visibility.Visible;
        BrowserErrorBar.Title = title;
        BrowserErrorBar.Message = message;
        UiMotion.Open(BrowserErrorBar);
        SetHubStatus(message);
    }

    private void UpdateEmptyNotices()
    {
        if (CookieEmptyNotice != null)
        {
            UiMotion.SetVisible(CookieEmptyNotice, _filteredCookies.Count == 0, MotionCue.Fade);
        }

        if (HistoryEmptyNotice != null)
        {
            UiMotion.SetVisible(HistoryEmptyNotice, HistoryListView.Items.Count == 0, MotionCue.Fade);
        }
    }

    private void SetHubStatus(string message)
    {
        _hubStatus = message;
        if (HubStatusText != null) HubStatusText.Text = message;
    }

    public void FocusAddressBar()
    {
        CancelInitialAddressFocus();
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
