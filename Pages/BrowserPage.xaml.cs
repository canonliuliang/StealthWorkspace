using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using StealthWorkspace.Controls;
using StealthWorkspace.Services;

namespace StealthWorkspace.Pages;

public sealed partial class BrowserPage : Page
{
    private readonly HashSet<TabViewItem> _closingTabs = new();
    private readonly Dictionary<TabViewItem, TabViewItem> _tabOpeners = new();

    private bool _isPageLoaded;
    private bool _isBurningAll;
    private bool _confirmingEnd;
    private bool _confirmingCloseOthers;
    private bool _isUnloading;
    private bool _browserFullScreen;
    private BrowserTabControl? _selectedTab;
    internal UIElement NavigationChrome => _selectedTab?.NavigationChrome ?? TabSearchButton;

    public BrowserPage()
    {
        InitializeComponent();
        PreviewKeyDown += BrowserPage_PreviewKeyDown;
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += BrowserPage_Loaded;
        Unloaded += BrowserPage_Unloaded;
        VaultService.Instance.Locked += BrowserVaultLocked;
    }

    #region Page lifetime

    private void BrowserPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isPageLoaded = true;
        _selectedTab?.SetTabSelected(true);

        // A fresh page instance starts with an empty TabView; only create the initial
        // tab here so navigating away/back never races with the previous instance's
        // asynchronous teardown.
        if (BrowserTabView.TabItems.Count == 0)
        {
            CreateNewTab();
        }
    }

    private void BrowserPage_Unloaded(object sender, RoutedEventArgs e)
    {
        CloseTabSearch();
        _isPageLoaded = false;
        _selectedTab?.SetTabSelected(false);
        RefreshPageFullScreen();
        if (!VaultService.Instance.IsUnlocked) _ = ClearLockedSessionAsync();
    }

    private void BrowserVaultLocked() => _ = ClearLockedSessionAsync();

    private async Task ClearLockedSessionAsync()
    {
        // Re-entrancy guard: Unloaded can be raised more than once while the page is
        // being torn down. Only the first run releases the tabs.
        if (_isUnloading) return;
        _isUnloading = true;

        try
        {
            Task closing = ClosePageTabsAsync();
            if (!_isBurningAll)
            {
                if (!await BrowserProfileManager.EndSessionAsync(() => closing))
                {
                    AppLog.Warn("BrowserPage_Unloaded: 会话目录未清除，将在退出时重试");
                }
            }
            else await closing;

            if (_browserFullScreen)
            {
                _browserFullScreen = false;
                BrowserTabView.Margin = new Thickness(0);
                try
                {
                    MainWindow.Current?.SetFullScreenMode(false);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"BrowserPage_Unloaded: 退出全屏失败（{ex.GetType().Name}）");
                }
            }

        }
        catch (Exception ex)
        {
            AppLog.Error("BrowserPage_Unloaded failed", ex);
        }
        finally
        {
            _isUnloading = false;

            // If the page was re-loaded while the old tabs were still being released,
            // Loaded could not create a replacement tab (the unload guard was active).
            // Make sure the TabView is never left empty after disposal finishes.
            EnsureTabExists();
        }
    }

    private Task ClosePageTabsAsync()
    {
        CloseTabSearch();
        BrowserDownloads.EndSession();
        BrowserSessionHistory.Clear();
        _tabOpeners.Clear();
        List<TabViewItem> tabs = BrowserTabView.TabItems.OfType<TabViewItem>().ToList();
        BrowserTabView.TabItems.Clear();
        var closing = new List<Task>(tabs.Count);
        foreach (var tab in _downloadOwners.Keys.ToArray()) closing.Add(tab.DisposeTabAsync());
        DetachDownloadOwners();
        foreach (TabViewItem item in tabs)
        {
            if (item.Content is BrowserTabControl tab) closing.Add(tab.DisposeTabAsync());
            ReleaseClosedTabItem(item);
        }
        return Task.WhenAll(closing);
    }

    #endregion

    #region Tab creation

    private TabViewItem? CreateNewTab(string? initialUrl = null, [System.Runtime.CompilerServices.CallerMemberName] string origin = "", bool select = true, int? position = null)
    {
        if (!_isPageLoaded || _isUnloading || _isBurningAll || !VaultService.Instance.IsUnlocked) return null;
        if (SmokeTest.Enabled) SmokeTest.Report($"tab-create: origin={origin}; existing={BrowserTabView.TabItems.Count}; live={BrowserTabControl.LiveTabCount}");

        var tabItem = new TabViewItem
        {
            Header = "新标签页",
            IconSource = new FontIconSource { Glyph = "\uE774" },
            IsClosable = true,
        };

        var tabControl = new BrowserTabControl();
        if (BrowserTabView.SelectedItem is TabViewItem opener) _tabOpeners[tabItem] = opener;
        if (initialUrl == null) tabControl.FocusAddressWhenLoaded();

        tabControl.TitleChanged += title =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!BrowserTabView.TabItems.Contains(tabItem)) return;
                tabItem.Header = title;
                ToolTipService.SetToolTip(tabItem, title);
                ScheduleOpenTabRefresh();
            });
        };

        tabControl.DocumentAddressChanged += ScheduleOpenTabRefresh;
        tabControl.AudioPlayingChanged += isPlaying =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!BrowserTabView.TabItems.Contains(tabItem)) return;
                tabItem.IconSource = tabControl.TabIcon;
                ScheduleOpenTabRefresh();
            });
        };

        tabControl.NewTabRequested += (url, background) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // The originating tab may have closed while this request was queued.
                if (BrowserTabView.TabItems.Contains(tabItem) && !_isUnloading && !_isBurningAll &&
                    CreateNewTab(url, "NewTabRequested", select: !background) is { } opened)
                    _tabOpeners[opened] = tabItem;
            });
        };

        tabControl.PopupRequested = async background =>
        {
            if (!BrowserTabView.TabItems.Contains(tabItem)) return null;
            var childItem = CreateNewTab(origin: "WebPopup", select: !background);
            if (childItem?.Content is not BrowserTabControl child) return null;
            _tabOpeners[childItem] = tabItem;
            try
            {
                if (await child.PreparePopupAsync() && BrowserTabView.TabItems.Contains(tabItem) &&
                    BrowserTabView.TabItems.Contains(childItem)) return child;
            }
            catch (Exception ex) { AppLog.Warn("Popup initialization failed: " + ex.GetType().Name); }
            await CloseTabAsync(childItem);
            return null;
        };
        tabControl.CloseRequested += () => DispatcherQueue.TryEnqueue(() => { _ = CloseTabAsync(tabItem); });

        // Read the current selection when the queued event is delivered. An old
        // background tab or a tab already closed must never hide the window chrome.
        tabControl.FullScreenModeChanged += _ => DispatcherQueue.TryEnqueue(RefreshPageFullScreen);

        tabControl.NavigationVisualChanged += () =>
        {
            if (!BrowserTabView.TabItems.Contains(tabItem)) return;
            tabItem.IconSource = tabControl.TabIcon;
            ScheduleOpenTabRefresh();
        };
        var menu = new MenuFlyout();
        var duplicate = new MenuFlyoutItem { Text = "复制标签页" };
        duplicate.Click += (_, _) => CreateNewTab(tabControl.CurrentUrl);
        var mute = new MenuFlyoutItem { Text = "静音 / 取消静音" };
        mute.Click += (_, _) => tabControl.ToggleAudioMute();
        var closeOthers = new MenuFlyoutItem { Text = "关闭其他标签页" };
        closeOthers.Click += async (_, _) => await CloseOtherTabsAsync(tabItem);
        var close = new MenuFlyoutItem { Text = "关闭标签页    Ctrl+W" };
        close.Click += async (_, _) => await CloseTabAsync(tabItem);
        menu.Items.Add(duplicate); menu.Items.Add(mute); menu.Items.Add(closeOthers); menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(close);
        tabItem.ContextFlyout = menu;

        tabItem.Content = tabControl;

        if (position is int index) BrowserTabView.TabItems.Insert(Math.Clamp(index, 0, BrowserTabView.TabItems.Count), tabItem);
        else BrowserTabView.TabItems.Add(tabItem);
        if (select) BrowserTabView.SelectedItem = tabItem;
        UpdateTabCount();

        // An empty tab shows a local start page and does not create a WebView2 session.
        if (initialUrl != null) DispatcherQueue.TryEnqueue(() =>
        {
            if (!BrowserTabView.TabItems.Contains(tabItem)) return;
            tabControl.NavigateToText(initialUrl);
        });
        return tabItem;
    }

    private void EnsureTabExists()
    {
        try
        {
            if (!_isPageLoaded || _isUnloading || _isBurningAll) return;
            if (BrowserTabView.TabItems.Count > 0) return;

            CreateNewTab();
        }
        catch (Exception ex)
        {
            AppLog.Error("EnsureTabExists failed", ex);
        }
    }

    internal async Task<bool> RunSmokeTestAsync()
    {
        long constructorTicks = 0, constructorBytes = 0;
        for (int i = 0; i < 12; i++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var probe = new BrowserTabControl();
            constructorTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            constructorBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (probe.HasAuxiliaryUi || probe.HasBrowserControl) throw new InvalidOperationException("Blank tab eagerly loaded hidden UI");
            await probe.DisposeTabAsync();
        }
        SmokeTest.Report($"browser: blank control construction avg ms={constructorTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 12:F2}; managed KiB={constructorBytes / 1024.0 / 12:F1}");
        var firstItem = BrowserTabView.SelectedItem as TabViewItem;
        if (firstItem?.Content is not BrowserTabControl first || first.HasBrowserControl) return false;
        if (!await first.RunSmokeNavigationAsync(checkSleep: true)) return false;

        CreateNewTab();
        BrowserTabControl? second = (BrowserTabView.SelectedItem as TabViewItem)?.Content as BrowserTabControl;
        var secondItem = BrowserTabView.SelectedItem as TabViewItem;
        bool lazy = second != null && !ReferenceEquals(first, second) && !second.HasBrowserControl;
        SmokeTest.Report("browser: new tabs defer WebView2 control creation=" + lazy);
        if (!lazy || second == null) return false;
        bool mutedDuringInitialization;
        try
        {
            PrivacyMediaGuard.SetAppAudioMuted(true);
            if (!await second.RunSmokeNavigationAsync()) return false;
            second.ToggleAudioMute();
            mutedDuringInitialization = first.IsAudioMuted && second.IsAudioMuted;
        }
        finally { PrivacyMediaGuard.SetAppAudioMuted(false); }
        bool privacyMute = mutedDuringInitialization && !first.IsAudioMuted && !second.IsAudioMuted;
        SmokeTest.Report("browser: privacy mute covers background and newly initialized tabs=" + privacyMute);
        if (!privacyMute) return false;
        int countBeforeConfirm = BrowserTabView.TabItems.Count;
        BurnAllTabs_Click(EndSessionBtn, new RoutedEventArgs());
        bool prompted = _confirmingEnd && !_isBurningAll;
        await Task.Delay(150);
        WorkspaceDialogs.DismissForPrivacy();
        for (int i = 0; i < 50 && _confirmingEnd; i++) await Task.Delay(50);
        bool preservedTabs = prompted && !_confirmingEnd && BrowserTabView.TabItems.Count == countBeforeConfirm &&
            ReferenceEquals(BrowserTabView.SelectedItem, secondItem) && second.HasBrowserControl;
        SmokeTest.Report("browser: clicking end session prompts and dismissal preserves loaded tabs=" + preservedTabs);
        if (!preservedTabs) return false;
        Task closeOthers = CloseOtherTabsAsync(secondItem!);
        await Task.Delay(120);
        bool confirmingOthers = _confirmingCloseOthers;
        WorkspaceDialogs.DismissForPrivacy();
        await closeOthers;
        if (!confirmingOthers || BrowserTabView.TabItems.Count != countBeforeConfirm || !first.HasBrowserControl) return false;
        SmokeTest.Report("browser: cancelling close-other-tabs preserves both loaded pages=True");
        if (!await second.HasSmokeCookieAsync(create: true)) return false;

        var sleep = await first.CheckSmokeBackgroundSleepAsync();
        SmokeTest.Report($"browser: background suspended={sleep.Suspended}, renderer CPU {sleep.AwakeCpuMs:0}ms -> {sleep.AsleepCpuMs:0}ms per 400ms, active tab awake={second.IsSmokeBrowserAwake}");
        if (!sleep.Suspended || !second.IsSmokeBrowserAwake) return false;
        BrowserTabView.SelectedItem = firstItem;
        bool resumed = await first.VerifySmokeResumeAsync();
        SmokeTest.Report("browser: resumed with unsaved page state=" + resumed);
        if (!resumed) return false;
        bool covered;
        try
        {
            BrowserTabControl.SetPrivacyCoverActive(true);
            for (int i = 0; i < 100 && !(first.IsSmokeSuspended && second.IsSmokeSuspended); i++) await Task.Delay(100);
            covered = first.IsAudioMuted && second.IsAudioMuted && first.IsSmokeSuspended && second.IsSmokeSuspended;
        }
        finally { BrowserTabControl.SetPrivacyCoverActive(false); }
        bool coverRestored = !first.IsAudioMuted && !second.IsAudioMuted && await first.VerifySmokeResumeAsync();
        SmokeTest.Report($"browser: privacy cover muted and suspended every tab={covered}; restored selected document={coverRestored}");
        if (!covered || !coverRestored) return false;
        if (MainWindow.Current?.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            try
            {
                presenter.Minimize();
                for (int i = 0; i < 250 && !first.IsSmokeSuspended; i++) await Task.Delay(100);
                bool minimized = first.IsSmokeSuspended;
                SmokeTest.Report($"browser: selected tab suspends when window minimized={minimized}; presenter={presenter.State}; nativeMinimized={BossKeyService.Instance.IsWindowMinimized}");
                if (!minimized) return false;
            }
            finally { presenter.Restore(); }
            await Task.Delay(250);
            bool restored = await first.VerifySmokeResumeAsync();
            SmokeTest.Report("browser: restoring window resumes selected tab and retains DOM=" + restored);
            if (!restored) return false;
        }
        bool recovered = await first.CheckSmokeCrashRecoveryAsync();
        SmokeTest.Report("browser: renderer crash reloads without replacing the download controller=" + recovered);
        if (!recovered) return false;
        BrowserTabView.SelectedItem = secondItem;

        await CloseTabAsync(firstItem);
        bool preserved = await second.HasSmokeCookieAsync();
        SmokeTest.Report("browser: closing one tab preserves the shared session=" + preserved);
        if (!preserved) return false;
        if (!await second.CheckSmokeHttpAsync()) return false;
        if (!await second.CheckSmokeChromeAsync()) return false;
        if (!await second.CheckFaviconFormatsAsync()) return false;
        if (!await second.CheckSmokeDeferredPrivacyAsync()) return false;
        if (!await second.CheckSmokeFindAsync()) return false;
        long memoryBefore = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
        var retiredControls = new List<WeakReference<BrowserTabControl>>();
        for (int i = 0; i < 40; i++)
        {
            CreateNewTab();
            if (BrowserTabView.SelectedItem is not TabViewItem stressItem ||
                stressItem.Content is not BrowserTabControl stress || !await stress.RunSmokeNavigationAsync()) return false;
            await CloseTabAsync(stressItem);
            retiredControls.Add(new WeakReference<BrowserTabControl>(stress));
            stress = null!;
            stressItem = null!;
        }
        BrowserTabView.SelectedItem = secondItem;
        await Task.Delay(500);
        // Diagnostic only: prove closed native/managed event cycles are broken.
        // Normal browsing and the time-based endurance run never force a GC.
        GC.Collect();
        await Task.Delay(1000);
        GC.Collect();
        await Task.Delay(500);
        int survivingClosed = retiredControls.Count(reference => reference.TryGetTarget(out _));
        long memoryAfter = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
        SmokeTest.Report($"browser: 40 open/load/close cycles; live tabs={BrowserTabControl.LiveTabCount}; closed controls retained after diagnostic GC={survivingClosed}; app private memory MiB={memoryBefore / 1048576.0:F1}->{memoryAfter / 1048576.0:F1}");
        if (BrowserTabControl.LiveTabCount != 1 || survivingClosed > 2) return false;
        string? oldProfile = BrowserProfileManager.ActiveSessionDirectory;

        bool cleared = await BurnAllTabsAsync() && oldProfile != null && !System.IO.Directory.Exists(oldProfile);
        SmokeTest.Report("browser: old session removed=" + cleared);
        BrowserTabControl? fresh = (BrowserTabView.SelectedItem as TabViewItem)?.Content as BrowserTabControl;
        if (!cleared || fresh == null || fresh.HasBrowserControl || !await fresh.RunSmokeNavigationAsync()) return false;

        bool rotated = BrowserProfileManager.ActiveSessionDirectory is { } current &&
            !string.Equals(current, oldProfile, StringComparison.OrdinalIgnoreCase) && !await fresh.HasSmokeCookieAsync();
        SmokeTest.Report("browser: local page loaded in a new session=" + rotated);
        return rotated && await CheckTabManagementSmokeAsync();
    }

    internal BrowserTabControl? SmokeSelectedTab => _selectedTab;
    internal bool SmokeSessionIsEmpty => BrowserSessionHistory.RecentlyClosed.Count == 0 && BrowserTabView.TabItems.OfType<TabViewItem>().All(t => t.Content is BrowserTabControl tab && !tab.HasBrowserControl);

    private async Task<bool> CheckTabManagementSmokeAsync()
    {
        await using var fixture = new BrowserHttpFixture();
        CreateNewTab(fixture.Address + "/page");
        var item = (TabViewItem)BrowserTabView.SelectedItem;
        var tab = (BrowserTabControl)item.Content;
        for (int i = 0; i < 100 && (!tab.IsSmokeNavigationComplete || tab.CurrentTitle != "HTTP fixture"); i++) await Task.Delay(100);
        if (tab.CurrentTitle != "HTTP fixture") return false;
        string url = tab.CurrentUrl;
        int position = BrowserTabView.TabItems.IndexOf(item);
        var neighbor = CreateNewTab(fixture.Address + "/page?neighbor")!;
        await WaitAuditAsync(() => ((BrowserTabControl)neighbor.Content).CurrentTitle == "HTTP fixture");
        var trailing = CreateNewTab()!;
        int count = BrowserTabView.TabItems.Count;
        await CloseTabAsync(item);
        await CloseTabAsync(neighbor);
        TabSearchScope.SelectedItem = ClosedTabsSection;
        TabSearchBox.Text = url;
        RefreshTabList();
        var wanted = TabSearchResults.Items.OfType<Models.BrowserTabSearchItem>().SingleOrDefault(row => row.Address == url);
        if (wanted == null) return false;
        ActivateSearchTab(wanted);
        var reopened = (BrowserTabControl)((TabViewItem)BrowserTabView.SelectedItem).Content;
        for (int i = 0; i < 100 && (!reopened.IsSmokeNavigationComplete || reopened.CurrentTitle != "HTTP fixture"); i++) await Task.Delay(100);
        bool restored = BrowserTabView.TabItems.Count == count - 1 && BrowserTabView.SelectedIndex == position &&
            BrowserSessionHistory.RecentlyClosed[0].Url.EndsWith("?neighbor", StringComparison.Ordinal) &&
            reopened.CurrentUrl == url && reopened.CurrentTitle == "HTTP fixture";
        SmokeTest.Report("browser: older closed tab restores its address and strip position, newer record retained=" + restored);
        ReopenClosedTab();
        var latest = (TabViewItem)BrowserTabView.SelectedItem;
        await WaitAuditAsync(() => ((BrowserTabControl)latest.Content).CurrentUrl.EndsWith("?neighbor", StringComparison.Ordinal));
        await CloseTabAsync(latest);
        await CloseTabAsync(trailing);
        BrowserTabView.SelectedItem = BrowserTabView.TabItems.OfType<TabViewItem>().Single(t => ReferenceEquals(t.Content, reopened));
        TabSearchScope.SelectedItem = OpenTabsSection;
        TabSearchBox.Text = "HTTP fixture";
        RefreshTabList();
        bool filtered = TabSearchResults.Items.Count == 1;
        var byTitle = TabSearchResults.SelectedItem;
        TabSearchBox.Text = fixture.Address;
        FilterTabList();
        filtered &= byTitle != null && ReferenceEquals(byTitle, TabSearchResults.SelectedItem);
        ClearTabSearch();
        SmokeTest.Report("browser: tab search finds the same row by title and address without rereading WebViews=" + filtered);
        if (!restored || !filtered) return false;
        return await reopened.RunSmokeNavigationAsync(checkSleep: true);
    }

    #endregion

    #region Tab closing & burning

    private async Task CloseOtherTabsAsync(TabViewItem keep)
    {
        if (_confirmingCloseOthers || _confirmingEnd || _isBurningAll || _isUnloading ||
            !BrowserTabView.TabItems.Contains(keep)) return;
        var others = BrowserTabView.TabItems.OfType<TabViewItem>().Where(t => t != keep).ToArray();
        if (others.Length == 0) return;
        _confirmingCloseOthers = true;
        try
        {
            if (others.Any(t => t.Content is BrowserTabControl { HasBrowserControl: true }))
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = $"关闭其他 {others.Length} 个标签页？",
                    Content = "将保留此标签页。其他网页未提交的表单内容可能丢失，进行中的下载会继续。",
                    PrimaryButtonText = "关闭其他标签页", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
                };
                if (await WorkspaceDialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
            }
            if (_isUnloading || _isBurningAll || !BrowserTabView.TabItems.Contains(keep) ||
                !VaultService.Instance.IsUnlocked || MainWindow.Current?.IsPrivacyCoverVisible == true) return;
            BrowserTabView.SelectedItem = keep;
            foreach (var other in others) await CloseTabAsync(other);
        }
        catch (Exception ex) { AppLog.Warn("Close other tabs: " + ex.GetType().Name); }
        finally { _confirmingCloseOthers = false; }
    }

    private async Task CloseTabAsync(TabViewItem? tabItem)
    {
        if (tabItem == null) return;
        if (!BrowserTabView.TabItems.Contains(tabItem)) return;
        if (!_closingTabs.Add(tabItem)) return; // already closing / already disposed

        int closedPosition = BrowserTabView.TabItems.IndexOf(tabItem);

        try
        {
            // Remove first: while the WebView2 is being torn down the TabView can no
            // longer re-enter TabCloseRequested/SelectionChanged for this item.
            try
            {
                // Return to the page that opened this tab, without briefly waking
                // an unrelated neighbor (which also reset its idle sleep timer).
                if (ReferenceEquals(BrowserTabView.SelectedItem, tabItem) && _tabOpeners.TryGetValue(tabItem, out var opener) && BrowserTabView.TabItems.Contains(opener))
                    BrowserTabView.SelectedItem = opener;
                foreach (var key in _tabOpeners.Where(entry => ReferenceEquals(entry.Key, tabItem) || ReferenceEquals(entry.Value, tabItem)).Select(entry => entry.Key).ToArray()) _tabOpeners.Remove(key);
                BrowserTabView.TabItems.Remove(tabItem);
                if (!_isBurningAll && !_isUnloading && tabItem.Content is BrowserTabControl closed)
                    BrowserSessionHistory.RememberClosed(closed.CurrentTitle, closed.CurrentUrl, closedPosition);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"CloseTabAsync: 从 TabView 移除标签失败（{ex.GetType().Name}）");
            }

            if (tabItem.Content is BrowserTabControl tabControl)
            {
                ReleaseClosedTabItem(tabItem);
                try
                {
                    if (!_isBurningAll && !_isUnloading && tabControl.HasPendingDownloads)
                        RetainDownloadOwner(tabControl);
                    else await tabControl.DisposeTabAsync();
                }
                catch (Exception ex)
                {
                    AppLog.Error("CloseTabAsync: 释放标签页失败", ex);
                }
            }

            // Disposal finished: make sure the TabView is never left completely empty.
            EnsureTabExists();
            AppLog.Info("CloseTabAsync: 已关闭一个标签页");
        }
        catch (Exception ex)
        {
            AppLog.Error("CloseTabAsync failed", ex);
        }
        finally
        {
            _closingTabs.Remove(tabItem);
            UpdateTabCount();
        }
    }

    private static void ReleaseClosedTabItem(TabViewItem item)
    {
        // Native flyout events capture both the tab and page. Detach the menu
        // contents as well as the visual content so closed tabs can be collected.
        if (item.ContextFlyout is MenuFlyout menu) menu.Items.Clear();
        item.ContextFlyout = null;
        item.IconSource = null;
        ToolTipService.SetToolTip(item, null);
        item.Content = null;
    }

    private async Task<bool> BurnAllTabsAsync()
    {
        if (!_isPageLoaded || _isBurningAll || _isUnloading) return false;
        _isBurningAll = true;
        CloseTabSearch();
        _tabOpeners.Clear();
        EndSessionBtn.IsEnabled = false;
        BrowserTabView.IsAddTabButtonVisible = false;
        SessionNotice.Title = "正在结束会话";
        SessionNotice.Message = "正在关闭标签并清除本次浏览数据。";
        SessionNotice.Severity = InfoBarSeverity.Informational;
        UiMotion.Open(SessionNotice);

        try
        {
            bool cleaned = await BrowserProfileManager.EndSessionAsync(() =>
            {
                List<TabViewItem> tabs = BrowserTabView.TabItems.OfType<TabViewItem>().ToList();
                BrowserTabView.TabItems.Clear();
                Task closing = BrowserTabControl.ShutdownAllAsync();
                DetachDownloadOwners();
                foreach (TabViewItem tab in tabs) ReleaseClosedTabItem(tab);
                return closing;
            });

            if (cleaned) SessionNotice.IsOpen = false;
            else
            {
                SessionNotice.Title = "旧会话数据尚未完全清除";
                SessionNotice.Message = "新的标签页将使用新会话。可再次结束会话重试，或在退出时重试。";
                SessionNotice.Severity = InfoBarSeverity.Warning;
                UiMotion.Open(SessionNotice);
            }
            return cleaned;
        }
        catch (Exception ex)
        {
            AppLog.Error("BurnAllTabs failed", ex);
            SessionNotice.Title = "无法结束会话";
            SessionNotice.Message = "请重试。";
            SessionNotice.Severity = InfoBarSeverity.Error;
            UiMotion.Open(SessionNotice);
            return false;
        }
        finally
        {
            if (_browserFullScreen)
            {
                _browserFullScreen = false;
                BrowserTabView.Margin = new Thickness(0);
                MainWindow.Current?.SetFullScreenMode(false);
            }
            _isBurningAll = false;
            EndSessionBtn.IsEnabled = true;
            BrowserTabView.IsAddTabButtonVisible = true;
            EnsureTabExists();
        }
    }

    private async void BrowserTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        await CloseTabAsync(args.Tab);
    }

    private void BrowserTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = (BrowserTabView.SelectedItem as TabViewItem)?.Content as BrowserTabControl;
        if (ReferenceEquals(selected, _selectedTab)) return;
        _selectedTab?.SetTabSelected(false);
        _selectedTab = selected;
        _selectedTab?.SetTabSelected(true);
        RefreshPageFullScreen();
    }

    private void BrowserTabView_AddTabButtonClick(TabView sender, object args)
    {
        if (_isBurningAll || _isUnloading) return;
        CreateNewTab();
    }

    private async void BurnAllTabs_Click(object sender, RoutedEventArgs e) => await RequestEndSessionAsync();

    private async Task RequestEndSessionAsync()
    {
        if (_confirmingEnd || _confirmingCloseOthers || _isBurningAll || _isUnloading) return;
        int pending = BrowserDownloads.PendingCount;
        if (BrowserProfileManager.ActiveSessionDirectory != null || pending > 0 ||
            BrowserTabView.TabItems.OfType<TabViewItem>().Any(item => item.Content is BrowserTabControl { HasBrowserControl: true }))
        {
            _confirmingEnd = true;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "结束本次浏览会话？",
                    Content = "将关闭全部标签页，清除本次浏览记录、最近关闭的标签及网站登录状态。未提交的表单内容可能丢失。" +
                        (pending > 0 ? $"\n{pending} 个未完成下载也将取消。" : ""),
                    PrimaryButtonText = "结束会话", CloseButtonText = "继续浏览", DefaultButton = ContentDialogButton.Close
                };
                if (await WorkspaceDialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
            }
            catch (Exception ex) { AppLog.Warn("Confirm end session: " + ex.GetType().Name); return; }
            finally { _confirmingEnd = false; }
        }
        if (VaultService.Instance.IsUnlocked && MainWindow.Current?.IsPrivacyCoverVisible != true) await BurnAllTabsAsync();
    }

    #endregion

    #region Keyboard accelerators

    private void UpdateTabCount()
    {
        TabCountText.Text = BrowserTabView.TabItems.Count.ToString();
        if (_tabSearchOpen) RefreshTabList();
    }

    private void ReopenClosedTab(ClosedBrowserTab? closed = null)
    {
        closed ??= BrowserSessionHistory.RecentlyClosed.FirstOrDefault();
        if (closed != null && BrowserSessionHistory.RecentlyClosed.Contains(closed) &&
            CreateNewTab(closed.Url, position: closed.Position) != null)
            BrowserSessionHistory.RemoveClosed(closed);
    }

    private void ReopenTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; ReopenClosedTab(); }
    private void SwitchTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        int count = BrowserTabView.TabItems.Count;
        if (count == 0) return;
        int direction = sender.Modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift) ? -1 : 1;
        BrowserTabView.SelectedIndex = (BrowserTabView.SelectedIndex + direction + count) % count;
        args.Handled = true;
    }
    private void NumberedTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        int index = sender.Key == Windows.System.VirtualKey.Number9 ? BrowserTabView.TabItems.Count - 1 : (int)sender.Key - (int)Windows.System.VirtualKey.Number1;
        if (index >= 0 && index < BrowserTabView.TabItems.Count) BrowserTabView.SelectedIndex = index;
        args.Handled = true;
    }

    private void NewTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_isBurningAll || _isUnloading) return;
        CreateNewTab();
    }

    private void FocusAddressAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if ((BrowserTabView.SelectedItem as TabViewItem)?.Content is BrowserTabControl tab)
        {
            tab.FocusAddressBar();
            args.Handled = true;
        }
    }

    private void FindAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_selectedTab == null || _selectedTab.IsNativeFindDispatching) return;
        _selectedTab.FindFromShortcut(sender.Key == Windows.System.VirtualKey.F3, sender.Modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift));
        args.Handled = true;
    }

    private void BookmarkAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_selectedTab == null) return;
        _selectedTab.ToggleBookmarkFromShortcut();
        args.Handled = true;
    }

    private void HistoryAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_selectedTab == null) return;
        _selectedTab.OpenSessionHistory();
        args.Handled = true;
    }

    private void CloseTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (BrowserTabView.SelectedItem is TabViewItem selectedTab)
        {
            _ = CloseTabAsync(selectedTab);
        }
    }

    private void BurnAllTabsAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = RequestEndSessionAsync();
    }

    #endregion
}
