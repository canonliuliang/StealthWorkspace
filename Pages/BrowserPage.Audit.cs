using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using StealthWorkspace.Controls;
using StealthWorkspace.Services;

namespace StealthWorkspace.Pages;

public sealed partial class BrowserPage
{
    internal async Task RunBrowserAuditAsync()
    {
        if (SmokeTest.RecoveryAuditOnly)
        {
            await CheckRecoveryWorkflowAsync();
            SmokeTest.Report("audit complete");
            return;
        }
        // Fullscreen/native keyboard input requires an actually foreground
        // window. A background test process must not impersonate user focus.
        SmokeTest.Report($"audit: waiting for test-window activation, pid={Environment.ProcessId}");
        for (int i = 0; i < 300 && !BrowserZoomInput.IsOwnerForeground(MainWindow.CurrentHandle); i++) await Task.Delay(100);
        if (!BrowserZoomInput.IsOwnerForeground(MainWindow.CurrentHandle)) throw new InvalidOperationException("Audit window must be activated");
        if (Environment.GetCommandLineArgs().Contains("--menu-actions-only"))
        {
            var selected = (BrowserTabControl)((TabViewItem)BrowserTabView.SelectedItem).Content;
            await selected.CheckMenuActionsAuditAsync();
            await CheckTabStripEndSessionAsync(selected);
            SmokeTest.Report("menu actions audit complete");
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--downloads-only"))
        {
            await CheckDownloadLifetimeAsync();
            SmokeTest.Report("download audit complete");
            return;
        }
        await CheckNewTabPickerAsync();
        if (Environment.GetCommandLineArgs().Contains("--new-tab-only"))
        {
            SmokeTest.Report("audit complete");
            return;
        }
        var firstItem = (TabViewItem)BrowserTabView.SelectedItem;
        var first = (BrowserTabControl)firstItem.Content;
        if (!await first.RunSmokeNavigationAsync(checkSleep: true)) throw new InvalidOperationException("Audit setup failed");
        AuditRequire(await first.CheckCookieCleanupAuditAsync(), "native site cookie cleanup includes nested paths and preserves other hosts");
        CreateNewTab();
        var secondItem = (TabViewItem)BrowserTabView.SelectedItem;
        var before = await first.CheckSmokeBackgroundSleepAsync();
        SmokeTest.Report($"audit: before DevTools call suspended={before.Suspended}, busy CPU={before.AwakeCpuMs:F0}, sleeping CPU={before.AsleepCpuMs:F0}");
        BrowserTabView.SelectedItem = firstItem;
        if (!await first.VerifySmokeResumeAsync()) throw new InvalidOperationException("Audit resume failed");
        await first.AttachAuditDevToolsAsync();
        BrowserTabView.SelectedItem = secondItem;
        var after = await first.CheckSmokeBackgroundSleepAsync();
        SmokeTest.Report($"audit: after one DevTools call suspended={after.Suspended}, busy CPU={after.AwakeCpuMs:F0}, sleeping CPU={after.AsleepCpuMs:F0}");
        await CheckPopupWorkflowAsync();
        await CheckPendingResourceSleepAsync();
        var reloadItem = CreateNewTab()!;
        await ((BrowserTabControl)reloadItem.Content).CheckHardReloadAuditAsync();
        await ((BrowserTabControl)reloadItem.Content).CheckSiteStorageAndAddressAuditAsync();
        await ((BrowserTabControl)reloadItem.Content).CheckBookmarkEditWorkflowAsync();
        await ((BrowserTabControl)reloadItem.Content).CheckNetworkPrivacyBoundaryAsync();
        await ((BrowserTabControl)reloadItem.Content).CheckShareLinkWorkflowAsync();
        await CloseTabAsync(reloadItem);
        await CheckFullScreenWorkflowAsync();
        var imageItem = CreateNewTab()!;
        await ((BrowserTabControl)imageItem.Content).CheckNativeZoomAuditAsync();
        await ((BrowserTabControl)imageItem.Content).CheckPageImagesAuditAsync();
        await CloseTabAsync(imageItem);
        CreateNewTab();
        await ((BrowserTabControl)((TabViewItem)BrowserTabView.SelectedItem).Content).CompareLoadingWithPlainWebViewAsync();
        await CheckDownloadLifetimeAsync();
        await CheckRecoveryWorkflowAsync();
        SmokeTest.Report("audit complete");
    }

    private async Task CheckTabStripEndSessionAsync(BrowserTabControl selected)
    {
        await using var fixture = new BrowserHttpFixture();
        string originalUrl = fixture.Address + "/page?before-session-end";
        await selected.NavigateAuditAsync(originalUrl);
        var originalTabs = BrowserTabView.TabItems.OfType<TabViewItem>().ToArray();
        var originalSelection = BrowserTabView.SelectedItem;

        Button? DialogButton(string name) => VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot)
            .Select(popup => BrowserTabControl.FindFixtureButton(popup.Child, name)).FirstOrDefault(button => button != null);
        async Task AnswerAsync(string name)
        {
            MediaAcceptance.Invoke(TabStripEndSession);
            await WaitAuditAsync(() => _confirmingEnd && DialogButton(name) != null);
            MediaAcceptance.Invoke(DialogButton(name)!);
        }

        await AnswerAsync("CloseButton");
        await WaitAuditAsync(() => !_confirmingEnd && DialogButton("CloseButton") == null);
        AuditRequire(ReferenceEquals(originalSelection, BrowserTabView.SelectedItem) &&
            originalTabs.SequenceEqual(BrowserTabView.TabItems.OfType<TabViewItem>()) && selected.HasBrowserControl && selected.CurrentUrl == originalUrl,
            "cancelling the tab-strip end-session dialog preserves all tabs and the loaded URL");

        await AnswerAsync("PrimaryButton");
        // The native profile cleanup is bounded separately and can take longer than an ordinary UI transition.
        await WaitAuditAsync(() => !_isBurningAll && !_confirmingEnd && BrowserTabView.TabItems.Count == 1 &&
            _selectedTab is { IsLoaded: true } fresh && !ReferenceEquals(selected, fresh), 30_000);
        var blank = _selectedTab!;
        AuditRequire(SmokeSessionIsEmpty && string.IsNullOrEmpty(blank.CurrentUrl) && !SessionNotice.IsOpen &&
            BrowserProfileManager.LastPersonalHistoryCleanupSucceeded && TabStripEndSession.IsEnabled &&
            BrowserTabView.IsAddTabButtonVisible, "confirming end-session clears native history and leaves one usable blank tab");
        await blank.NavigateAuditAsync(fixture.Address + "/page?after-session-end");
        AuditRequire(blank.CurrentUrl == fixture.Address + "/page?after-session-end" && BrowserTabView.TabItems.Count == 1,
            "the replacement blank tab opens the next page normally");
    }

    private async Task CheckRecoveryWorkflowAsync()
    {
        var recovery = (BrowserTabControl)CreateNewTab()!.Content;
        await recovery.CheckBusyRendererAuditAsync();
        await recovery.CheckRendererDownloadRecoveryAuditAsync();
        await using var fixture = new BrowserHttpFixture();
        var ownerItem = CreateNewTab()!;
        var owner = (BrowserTabControl)ownerItem.Content;
        await owner.NavigateAuditAsync(fixture.Address + "/page");
        var operation = await owner.BeginDownloadAuditAsync(fixture.Address + "/download-slow");
        var record = BrowserDownloads.Items.Single(item => item.IsPending && item.FilePath == operation.ResultFilePath);
        record.TogglePauseResume();
        await WaitAuditAsync(() => record.CanResume);
        await CloseTabAsync(ownerItem);
        await WaitAuditAsync(() => _downloadOwners.Count == 1);
        await recovery.CheckBrowserProcessRecoveryAuditAsync();
        await WaitAuditAsync(() => _downloadOwners.Count == 0);
        AuditRequire(!record.IsPending && !record.IsCompleted && record.Status.Contains("浏览器进程已退出"),
            "browser exit releases closed-tab download owner and reports terminal failure");
    }

    private async Task CheckPendingResourceSleepAsync()
    {
        await using var fixture = new BrowserHttpFixture();
        var item = CreateNewTab()!;
        var tab = (BrowserTabControl)item.Content;
        await tab.PreparePendingResourceAuditAsync(fixture.Address + "/pending-page");
        var foreground = CreateNewTab()!;
        var sleeping = await tab.CheckSmokeBackgroundSleepAsync();
        AuditRequire(sleeping.Suspended && tab.AuditHasPendingResources,
            "hidden DOM-ready tab sleeps while stalled resource is still pending");
        BrowserTabView.SelectedItem = item;
        AuditRequire(await tab.VerifySmokeResumeAsync() && tab.AuditHasPendingResources,
            "returning to pending page resumes it with unsaved form intact");
        await CloseTabAsync(item);
        await CloseTabAsync(foreground);
    }

    private async Task CheckNewTabPickerAsync()
    {
        // A normal new tab has no WebView. Capture exactly what this user sees
        // before any fixture navigation, then exercise the flyout they opened.
        await UiCapture.SaveAsync(this, "new-tab.png");
        TabListFlyout.ShowAt(TabSearchButton);
        try
        {
            await WaitAuditAsync(() => TabSearchContent.IsLoaded && TabSearchResults.Items.Count == 1);
            var row = (Models.BrowserTabSearchItem)TabSearchResults.Items[0];
            AuditRequire(row.CloseVisibility == Microsoft.UI.Xaml.Visibility.Collapsed,
                "closing the sole empty tab is not offered as a no-op");
            await Task.Delay(225);
            await UiCapture.SaveAsync(TabSearchContent, "open-tabs.png");
            TabSearchBox.Text = "no-match-for-synthetic-empty-tab";
            await WaitAuditAsync(() => TabSearchResults.Items.Count == 0 &&
                TabSearchEmpty.Visibility == Microsoft.UI.Xaml.Visibility.Visible);
            TabSearchScope.SelectedItem = ClosedTabsSection;
            TabSearchBox.Text = "";
            await WaitAuditAsync(() => TabSearchEmpty.Text.Contains("本次会话"));
            await UiCapture.SaveAsync(TabSearchContent, "recent-tabs-empty.png");
            AuditRequire(TabSearchResults.Items.Count == 0, "tab picker shows its two empty states");
        }
        finally
        {
            TabListFlyout.Hide();
            await WaitAuditAsync(() => !_tabSearchOpen);
        }
    }

    private static void AuditRequire(bool passed, string description)
    {
        SmokeTest.Report($"audit: {description}={passed}");
        if (!passed) throw new InvalidOperationException(description);
    }

    private async Task CheckFullScreenWorkflowAsync()
    {
        await using var fixture = new BrowserHttpFixture();
        var item = CreateNewTab()!;
        var tab = (BrowserTabControl)item.Content;
        await tab.NavigateAuditAsync(fixture.Address + "/fullscreen");
        var originalPresenter = MainWindow.Current!.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        bool wasMaximized = originalPresenter?.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
        originalPresenter?.Maximize();
        await Task.Delay(150);
        MainWindow.Current?.BringToForeground();
        await tab.ClickAuditAsync("fullscreen");
        await Task.Delay(250);
        SmokeTest.Report("fullscreen audit request: " + await tab.EvaluateAuditAsync("JSON.stringify({error:window.fullscreenError,enabled:document.fullscreenEnabled,element:!!document.fullscreenElement,focused:document.hasFocus()})"));
        await WaitAuditAsync(() => tab.IsPageFullScreen && _browserFullScreen);
        AuditRequire(MainWindow.Current?.AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen,
            "page fullscreen enters native window fullscreen");
        var other = CreateNewTab()!;
        await WaitAuditAsync(() => !_browserFullScreen && !tab.IsPageFullScreen);
        AuditRequire(MainWindow.Current?.AppWindow.Presenter.Kind != Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen && BrowserTabView.Margin.Top == 0,
            "switching away from fullscreen restores window and tabs");
        AuditRequire(MainWindow.Current?.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized },
            "leaving page fullscreen preserves the preceding maximized window");
        BrowserTabView.SelectedItem = item;
        await Task.Delay(100);
        AuditRequire(await tab.EvaluateAuditAsync("document.fullscreenElement === null && document.getElementById('draft').value === 'keep my draft'") == "true",
            "returning to fullscreen tab retains page state with normal toolbar");
        await tab.ClickAuditAsync("fullscreen");
        await WaitAuditAsync(() => _browserFullScreen);
        await CloseTabAsync(item);
        await WaitAuditAsync(() => !_browserFullScreen);
        AuditRequire(MainWindow.Current?.AppWindow.Presenter.Kind != Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen && BrowserTabView.Margin.Top == 0,
            "closing fullscreen tab restores window and tabs");
        await CloseTabAsync(other);
        if (!wasMaximized) originalPresenter?.Restore();
    }

    private static async Task WaitAuditAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < timeoutMs && !condition()) await Task.Delay(50);
        if (!condition()) throw new TimeoutException("Audit UI state not reached");
    }

    private async Task CheckPopupWorkflowAsync()
    {
        await using var fixture = new BrowserHttpFixture();
        var parentItem = CreateNewTab()!;
        var parent = (BrowserTabControl)parentItem.Content;
        await parent.NavigateAuditAsync(fixture.Address + "/popup");
        int originalCount = BrowserTabView.TabItems.Count;
        // Host ExecuteScriptAsync can carry a user gesture. Let the page's own
        // on-load timer create an actual unsolicited popup instead.
        await Task.Delay(650);
        AuditRequire(parent.AuditBlockedPopups == 1 && BrowserTabView.TabItems.Count == originalCount,
            "automatic popup blocked without creating a WebView");

        async Task<(TabViewItem Item, BrowserTabControl Tab)> OpenAsync(string id, bool background = false)
        {
            await parent.ClickAuditAsync(id, background);
            await WaitAuditAsync(() => BrowserTabView.TabItems.Count == originalCount + 1 && !parent.AuditPopupPending);
            var item = BrowserTabView.TabItems.OfType<TabViewItem>().Last();
            var tab = (BrowserTabControl)item.Content;
            await WaitAuditAsync(() => tab.IsSmokeNavigationComplete &&
                tab.CurrentTitle == (id == "blank" ? "Written child" : "Child fixture"));
            return (item, tab);
        }

        var child = await OpenAsync("open");
        AuditRequire(await child.Tab.EvaluateAuditAsync("!!window.opener && sessionStorage.getItem('popup-context')==='kept'") == "true",
            "child preserves opener and sessionStorage");
        AuditRequire(await parent.EvaluateAuditAsync("window.received > 0") == "true", "child can message opener");
        await child.Tab.EvaluateAuditAsync("window.close()");
        await WaitAuditAsync(() => BrowserTabView.TabItems.Count == originalCount);
        AuditRequire(ReferenceEquals(BrowserTabView.SelectedItem, parentItem), "window.close returns to opener");

        child = await OpenAsync("post");
        AuditRequire(fixture.LastPopupMethod == "POST", "new-tab form preserves POST method");
        await CloseTabAsync(child.Item);

        child = await OpenAsync("blank");
        AuditRequire(await child.Tab.EvaluateAuditAsync("document.getElementById('written')?.textContent") == "\"Written content\"",
            "about:blank popup accepts document.write");
        await CloseTabAsync(child.Item);

        child = await OpenAsync("background", background: true);
        AuditRequire(ReferenceEquals(BrowserTabView.SelectedItem, parentItem), "background open does not steal selected tab");
        await CloseTabAsync(child.Item);

        parent.SetAutomaticPopupsForSite(fixture.Address, true);
        await parent.NavigateAuditAsync(fixture.Address + "/popup?allowed");
        await WaitAuditAsync(() => BrowserTabView.TabItems.Count == originalCount + 1 && !parent.AuditPopupPending);
        var allowedChild = BrowserTabView.TabItems.OfType<TabViewItem>().Last();
        AuditRequire(!ReferenceEquals(allowedChild, parentItem), "site exception allows same-origin automatic popups");
        await CloseTabAsync(allowedChild);

        await parent.NavigateAuditAsync(fixture.Address + "/popup-frame");
        await WaitAuditAsync(() => parent.AuditBlockedPopups > 0);
        AuditRequire(BrowserTabView.TabItems.Count == originalCount, "embedded site does not inherit the top site's popup exception");

        await using var otherSite = new BrowserHttpFixture();
        await parent.NavigateAuditAsync(otherSite.Address + "/");
        await parent.NavigateAuditAsync(fixture.Address + "/popup?return");
        await WaitAuditAsync(() => parent.AuditBlockedPopups > 0);
        AuditRequire(BrowserTabView.TabItems.Count == originalCount, "leaving a site revokes its popup exception, including on return");
        await CloseTabAsync(parentItem);
    }

    private async Task CheckDownloadLifetimeAsync()
    {
        await using var fixture = new BrowserHttpFixture();
        var item = CreateNewTab()!;
        var tab = (BrowserTabControl)item.Content;
        await tab.NavigateAuditAsync(fixture.Address + "/page");
        var download = await tab.BeginDownloadAuditAsync(fixture.Address + "/download-slow");
        await WaitAuditAsync(() => download.BytesReceived > 0);
        SmokeTest.Report("download first bytes: " + tab.AuditDownloadSnapshot);
        await WaitAuditAsync(() => tab.AuditDownloadProgress.Contains('%'));
        AuditRequire(tab.AuditTracksDownload(download), "download is tracked while transferring");
        download.Pause();
        await WaitAuditAsync(() => download.State == Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Interrupted);
        AuditRequire(download.CanResume && tab.AuditTracksDownload(download), "paused download stays tracked for resume");
        long pausedBytes = download.BytesReceived;
        download.Resume();
        await WaitAuditAsync(() => download.State == Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.InProgress && download.BytesReceived > pausedBytes);
        AuditRequire(tab.AuditTracksDownload(download), "resumed download keeps progress and activity tracking");
        string path = download.ResultFilePath;
        var record = BrowserDownloads.Items.Single(i => i.FilePath == path);
        await CloseTabAsync(item);
        await WaitAuditAsync(() => tab.CurrentUrl == "about:blank");
        AuditRequire(_downloadOwners.Count == 1, "closed download tab retains only its blank controller");
        var blankItem = CreateNewTab()!;
        var blank = (BrowserTabControl)blankItem.Content;
        AuditRequire(await blank.OpenDownloadsAuditAsync() && !blank.HasBrowserControl,
            "new blank tab shows shared downloads without starting a WebView");
        await blank.CheckDownloadShortcutAuditAsync();
        AuditRequire(blank.HasPendingDownloadBadge, "new tab displays pending count for the closed tab's download");
        record.TogglePauseResume();
        await WaitAuditAsync(() => record.CanResume);
        AuditRequire(tab.AuditTracksDownload(download), "shared list can pause a download from a closed tab");
        record.TogglePauseResume();
        await WaitAuditAsync(() => record.IsCompleted && _downloadOwners.Count == 0);
        AuditRequire(!blank.HasPendingDownloadBadge, "completion clears pending badge across tabs");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        AuditRequire(bytes.Length == 4194304 && bytes.All(b => b == 0x5A), "closed-tab download completes with exact content and releases controller");
        await blank.NavigateAuditAsync(fixture.Address + "/page");
        await blank.CheckWebDownloadShortcutAuditAsync();
        var first = await blank.BeginDownloadAuditAsync(fixture.Address + "/download-slow?first");
        var survivor = await blank.BeginDownloadAuditAsync(fixture.Address + "/download-slow?second");
        var cancelled = BrowserDownloads.Items.Single(entry => entry.FilePath == first.ResultFilePath);
        var remaining = BrowserDownloads.Items.Single(entry => entry.FilePath == survivor.ResultFilePath);
        AuditRequire(first.ResultFilePath != survivor.ResultFilePath && survivor.ResultFilePath != path && first.ResultFilePath != path,
            "same-name concurrent downloads reserve separate files and preserve a previous completed file");
        await WaitAuditAsync(() => first.BytesReceived > 0 && survivor.BytesReceived > 0);
        cancelled.TogglePauseResume();
        await WaitAuditAsync(() => cancelled.CanResume);
        await CloseTabAsync(blankItem);
        cancelled.Cancel();
        await WaitAuditAsync(() => !cancelled.IsPending);
        await WaitAuditAsync(() => remaining.IsCompleted && _downloadOwners.Count == 0);
        var remainingBytes = await File.ReadAllBytesAsync(remaining.FilePath);
        AuditRequire(!cancelled.IsCompleted && cancelled.Status == "已取消" && !File.Exists(cancelled.FilePath) &&
            bytes.SequenceEqual(remainingBytes) && BrowserDownloads.PendingCount == 0,
            "canceling a paused download after closing its tab preserves the other file and releases the controller");
        var longName = new string('a', 115) + "🌲.zip";
        AuditRequire(BrowserDownloadPath.SanitizeName("جريدة.txt") == "جريدة.txt" &&
            BrowserDownloadPath.SanitizeName("研究\u202E表格.csv") == "研究_表格.csv" &&
            BrowserDownloadPath.SanitizeName(longName) == new string('a', 115) + ".zip",
            "download names retain ordinary international text and extensions, replace direction overrides and preserve Unicode scalar boundaries");
    }
}
