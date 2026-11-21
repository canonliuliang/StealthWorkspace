using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    internal async Task PreparePendingResourceAuditAsync(string address)
    {
        await InitializeAsync(null);
        var core = _hookedCore ?? throw new InvalidOperationException("Audit core missing");
        var ready = new TaskCompletionSource<bool>();
        void Ready(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args) => ready.TrySetResult(true);
        core.DOMContentLoaded += Ready;
        try
        {
            _backgroundIdleDelay = TimeSpan.FromMilliseconds(400);
            NavigateTo(new Uri(address));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!_documentReady || _activeNavigationId == null) throw new InvalidOperationException("Fixture must have usable DOM and pending resource");
        }
        finally { core.DOMContentLoaded -= Ready; }
    }

    internal bool AuditHasPendingResources => _documentReady && _activeNavigationId != null;

    internal async Task AttachAuditDevToolsAsync()
    {
        if (_hookedCore is not { } core) throw new InvalidOperationException("Audit core missing");
        await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", "{\"expression\":\"1\",\"returnByValue\":true}");
    }

    internal async Task<bool> CheckCookieCleanupAuditAsync()
    {
        var core = _hookedCore ?? throw new InvalidOperationException("Audit core missing");
        var manager = core.CookieManager;
        foreach (var (name, domain, path) in new[] {
            ("r11-domain", ".fixture.test", "/"), ("r11-host", "shop.fixture.test", "/"),
            ("r11-path", "shop.fixture.test", "/account"), ("r11-parent-host", "fixture.test", "/"),
            ("r11-lookalike", "notshop.fixture.test", "/"), ("r11-sibling", "other.fixture.test", "/")
        }) manager.AddOrUpdateCookie(manager.CreateCookie(name, "synthetic", domain, path));
        var before = await manager.GetCookiesAsync("");
        Services.SmokeTest.Report("cookie audit fixture domains: " + string.Join(",", before.Where(c => c.Name.StartsWith("r11-")).Select(c => c.Name + "=" + c.Domain)));
        int removed = await Services.BrowserSiteData.DeleteCookiesForHostAsync(core, "shop.fixture.test", () => !_isDisposed);
        var remaining = (await manager.GetCookiesAsync("")).Where(c => c.Name.StartsWith("r11-")).ToArray();
        Services.SmokeTest.Report("cookie audit deleted=" + removed + "; remaining=" + string.Join(",", remaining.Select(c => c.Name + "=" + c.Domain)));
        for (int retry = 0; retry < 20 && remaining.Length > 3; retry++)
        {
            await Task.Delay(50);
            remaining = (await manager.GetCookiesAsync("")).Where(c => c.Name.StartsWith("r11-")).ToArray();
        }
        Services.SmokeTest.Report("cookie audit settled remaining=" + string.Join(",", remaining.Select(c => c.Name + "=" + c.Domain)));
        bool ok = removed == 3 && remaining.Length == 3 && remaining.All(c => c.Name is "r11-parent-host" or "r11-lookalike" or "r11-sibling");
        foreach (var cookie in remaining) manager.DeleteCookie(cookie);
        return ok;
    }

    internal int AuditBlockedPopups => _blockedPopupCount;
    internal bool AuditPopupPending => _popupPending;
    internal bool AuditTracksDownload(CoreWebView2DownloadOperation operation) => _activeDownloads.Any(d => d.ResultFilePath == operation.ResultFilePath);
    internal string AuditDownloadProgress => DownloadToastDesc.Text;
    internal string AuditDownloadSnapshot => $"tracked={_activeDownloads.Count}; visible={DownloadToast.Visibility}; title={DownloadToastTitle.Text}; desc={DownloadToastDesc.Text}";

    internal async Task<bool> OpenDownloadsAuditAsync()
    {
        for (int i = 0; i < 40 && !IsLoaded; i++) await Task.Delay(25);
        var flyout = EnsureDownloadsFlyout();
        int opened = 0, closed = 0;
        string FocusDescription() => Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is Microsoft.UI.Xaml.FrameworkElement element
            ? element.GetType().Name + ":" + element.Name : "none";
        void Opened(object? sender, object args)
        {
            opened++;
            Services.SmokeTest.Report($"download flyout opened: focus={FocusDescription()}");
        }
        void Closed(object? sender, object args)
        {
            closed++;
            Services.SmokeTest.Report($"download flyout closed before audit hide: focus={FocusDescription()}; addressPending={_focusAddressOnLoad}");
        }
        flyout.Opened += Opened;
        flyout.Closed += Closed;
        try
        {
            // Exercise the real low-priority callback even when machine timing
            // has already delivered the original Loaded callback before this test.
            FocusAddressWhenLoaded();
            QueueInitialAddressFocus();
            Services.SmokeTest.Report($"download flyout before show: tabLoaded={IsLoaded}; anchorLoaded={DownloadsBtn.IsLoaded}; anchorSize={DownloadsBtn.ActualWidth:F1}x{DownloadsBtn.ActualHeight:F1}; addressPending={_focusAddressOnLoad}; focus={FocusDescription()}");
            ShowDownloads_Click(this, new Microsoft.UI.Xaml.RoutedEventArgs());
            bool cancelledInitialFocus = !_focusAddressOnLoad;
            var dispatched = new TaskCompletionSource<bool>();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => dispatched.TrySetResult(true));
            for (int i = 0; i < 40 && (flyout.Content is not DownloadListControl { IsLoaded: true } || opened == 0 || !dispatched.Task.IsCompleted); i++) await Task.Delay(25);
            bool loaded = flyout.Content is DownloadListControl { IsLoaded: true };
            bool stableOpen = flyout.IsOpen && opened > 0 && closed == 0 && dispatched.Task.IsCompleted;
            Services.SmokeTest.Report($"download flyout: tabLoaded={IsLoaded}; contentLoaded={loaded}; nativeControl={HasBrowserControl}; opened={opened}; closed={closed}; isOpen={flyout.IsOpen}; initialFocusCancelled={cancelledInitialFocus}; focusCallbackDrained={dispatched.Task.IsCompleted}; focus={FocusDescription()}");
            return loaded && stableOpen && cancelledInitialFocus;
        }
        finally
        {
            flyout.Opened -= Opened;
            flyout.Closed -= Closed;
            if (flyout.IsOpen)
            {
                var dismissed = new TaskCompletionSource<bool>();
                void Dismissed(object? sender, object args) => dismissed.TrySetResult(true);
                flyout.Closed += Dismissed;
                try
                {
                    flyout.Hide();
                    await dismissed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { flyout.Closed -= Dismissed; }
            }
        }
    }

    internal async Task CheckDownloadShortcutAuditAsync()
    {
        await RequireKeyboardForegroundAsync("download keyboard");
        var flyout = EnsureDownloadsFlyout();
        var editor = FindAddressEditor(UrlTextBox) ?? throw new InvalidOperationException("Missing address editor");
        if (!editor.Focus(Microsoft.UI.Xaml.FocusState.Keyboard) || !IsAddressBarFocused)
            throw new InvalidOperationException("The address editor did not receive keyboard focus");
        int opened = 0, closed = 0;
        void Opened(object? sender, object args) => opened++;
        void Closed(object? sender, object args) => closed++;
        flyout.Opened += Opened;
        flyout.Closed += Closed;
        try
        {
            if (flyout.IsOpen || !Services.BrowserZoomInput.SendDownloadsForAudit(MainWindow.CurrentHandle))
                throw new InvalidOperationException("Could not send Ctrl+J to the test window");
            for (int i = 0; i < 80 && opened == 0; i++) await Task.Delay(25);
            if (!flyout.IsOpen || opened != 1) throw new InvalidOperationException("Ctrl+J failed to show downloads from the address bar");
            if (!Services.BrowserZoomInput.SendDownloadsForAudit(MainWindow.CurrentHandle))
                throw new InvalidOperationException("Could not repeat Ctrl+J");
            await Task.Delay(250);
            if (!flyout.IsOpen || opened != 1 || closed != 0) throw new InvalidOperationException("Repeated Ctrl+J reopened or dismissed downloads");
            Services.SmokeTest.Report("download flyout: native Ctrl+J opened the shared downloads from the address bar; repeating it kept one flyout open");
        }
        finally
        {
            flyout.Opened -= Opened;
            flyout.Closed -= Closed;
            flyout.Hide();
        }
    }

    internal async Task CheckWebDownloadShortcutAuditAsync()
    {
        var core = _hookedCore ?? throw new InvalidOperationException("Missing focused document for Ctrl+J");
        var flyout = EnsureDownloadsFlyout();
        if (flyout.IsOpen || _browserControl?.Focus(Microsoft.UI.Xaml.FocusState.Keyboard) != true)
            throw new InvalidOperationException("Could not focus the webpage before Ctrl+J");
        for (int i = 0; i < 40 && await core.ExecuteScriptAsync("document.hasFocus()") != "true"; i++) await Task.Delay(25);
        if (await core.ExecuteScriptAsync("document.hasFocus()") != "true" || !Services.BrowserZoomInput.SendDownloadsForAudit(MainWindow.CurrentHandle))
            throw new InvalidOperationException("Could not send Ctrl+J while the document had keyboard focus");
        try
        {
            bool IsWorkspaceListVisible() => flyout.IsOpen && flyout.Content is DownloadListControl { IsLoaded: true } && !core.IsDefaultDownloadDialogOpen;
            for (int i = 0; i < 80 && !IsWorkspaceListVisible(); i++) await Task.Delay(25);
            await Task.Delay(200);
            if (!IsWorkspaceListVisible())
                throw new InvalidOperationException($"Webpage Ctrl+J failed to show only the workspace downloads; workspace={flyout.IsOpen}; content={flyout.Content?.GetType().Name}; engine={core.IsDefaultDownloadDialogOpen}");
            Services.SmokeTest.Report("download flyout: native Ctrl+J on the focused webpage opened only the workspace download list");
        }
        finally { flyout.Hide(); }
    }

    internal async Task<CoreWebView2DownloadOperation> BeginDownloadAuditAsync(string address)
    {
        var core = _hookedCore ?? throw new InvalidOperationException("Audit core missing");
        var starting = new TaskCompletionSource<CoreWebView2DownloadOperation>();
        void Started(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args) => starting.TrySetResult(args.DownloadOperation);
        core.DownloadStarting += Started;
        try
        {
            NavigateTo(new Uri(address));
            return await starting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { core.DownloadStarting -= Started; }
    }

    internal async Task NavigateAuditAsync(string address)
    {
        await InitializeAsync(null);
        var core = _hookedCore ?? throw new InvalidOperationException("Audit core missing");
        var ready = new TaskCompletionSource<bool>();
        void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => ready.TrySetResult(args.IsSuccess);
        core.NavigationCompleted += Completed;
        try
        {
            NavigateTo(new Uri(address));
            if (!await ready.Task.WaitAsync(TimeSpan.FromSeconds(15))) throw new InvalidOperationException("Audit navigation failed");
        }
        finally { core.NavigationCompleted -= Completed; }
    }

    internal Task<string> EvaluateAuditAsync(string script) => (_hookedCore ?? throw new InvalidOperationException("Audit core missing"))
        .ExecuteScriptAsync(script).AsTask().WaitAsync(TimeSpan.FromSeconds(4));

    internal async Task ClickAuditAsync(string id, bool background = false)
    {
        // Only synthetic loopback pages use this diagnostic user gesture. Normal
        // browsing relies on the runtime's trusted IsUserInitiated event value.
        if (_hookedCore is not { } core || !Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) || !uri.IsLoopback)
            throw new InvalidOperationException("Synthetic clicks are restricted to the loopback fixture");
        for (int i = 0; i < 100 && _browserControl?.IsLoaded != true; i++) await Task.Delay(25);
        _browserControl?.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        await Task.Delay(75);
        if (id == "fullscreen")
            Services.SmokeTest.Report($"fullscreen gesture: windowForeground={Services.BrowserZoomInput.IsOwnerForeground(MainWindow.CurrentHandle)}; loaded={_browserControl?.IsLoaded}; xamlFocus={_browserControl?.FocusState}; addressPending={_focusAddressOnLoad}; selected={_isSelected}; pageFocused={await core.ExecuteScriptAsync("document.hasFocus()")}");
        // Replay a slow release/renderer after a background press. A previously
        // used 750 ms cutoff lost the user's choice before WebView2 requested a tab.
        if (background)
        {
            _pointerPopupInBackground = true;
            await Task.Delay(900);
        }
        await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
        {
            expression = "document.getElementById(" + JsonSerializer.Serialize(id) + ").click()",
            userGesture = true
        }));
    }
}
