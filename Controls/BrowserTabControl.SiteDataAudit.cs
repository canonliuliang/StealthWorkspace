using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    internal async Task CheckSiteStorageAndAddressAuditAsync()
    {
        await using var site = new BrowserHttpFixture();
        await using var neighbor = new BrowserHttpFixture();
        await NavigateAuditAsync(site.Address + "/page");
        var core = _hookedCore ?? throw new InvalidOperationException("Site data fixture requires WebView2");
        async Task<bool> CheckAsync(string expression)
        {
            string reply = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true })).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
            using var json = JsonDocument.Parse(reply);
            return json.RootElement.GetProperty("result").TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.True;
        }
        const string seed = """
            (async () => {
              localStorage.setItem('r161-draft','saved');
              document.cookie = 'r161-login=kept;path=/';
              await (await caches.open('r161')).put('/r161-offline',new Response('saved'));
              await new Promise((resolve,reject) => {
                const open = indexedDB.open('r161');
                open.onupgradeneeded = () => open.result.createObjectStore('drafts');
                open.onsuccess = () => { open.result.close(); resolve(); };
                open.onerror = () => reject(open.error);
              });
              return true;
            })()
            """;
        const string preserved = """
            (async () => localStorage.getItem('r161-draft') === 'saved'
              && await (await caches.match('/r161-offline')).text() === 'saved'
              && (await indexedDB.databases()).some(db => db.name === 'r161')
              && document.cookie.includes('r161-login=kept'))()
            """;
        if (!await CheckAsync(seed)) throw new InvalidOperationException("Origin fixture initialization failed");
        await NavigateAuditAsync(neighbor.Address + "/page");
        if (!await CheckAsync(seed)) throw new InvalidOperationException("Adjacent port fixture initialization failed");
        string embeddedHost = site.Address.Replace("127.0.0.1", "localhost") + "/partition-host";
        await NavigateAuditAsync(embeddedHost + "?seed");
        const string embeddedPreserved = """
            (async () => { for (let i=0;i<40 && window.embeddedValue===undefined;i++) await new Promise(r=>setTimeout(r,25));
              return window.embeddedValue === 'embedded'; })()
            """;
        if (!await CheckAsync(embeddedPreserved)) throw new InvalidOperationException("Third-party partition fixture initialization failed");
        await NavigateAuditAsync(site.Address + "/page");
        if (!await CheckAsync(preserved) || !await CheckAsync("localStorage.getItem('r161-partition') === null"))
            throw new InvalidOperationException("First-party storage and embedded partition were not distinct before clearing");
        await BrowserSiteData.ClearOfflineDataForOriginAsync(core, new Uri(core.Source));
        await NavigateAuditAsync(site.Address + "/page?after");
        if (!await CheckAsync("""
            (async () => localStorage.getItem('r161-draft') === null && !(await caches.has('r161'))
              && !(await indexedDB.databases()).some(db => db.name === 'r161')
              && document.cookie.includes('r161-login=kept'))()
            """)) throw new InvalidOperationException("Origin clear did not preserve the expected data boundary");
        await NavigateAuditAsync(neighbor.Address + "/page");
        if (!await CheckAsync(preserved)) throw new InvalidOperationException("Origin clear affected a neighboring port");
        await NavigateAuditAsync(embeddedHost);
        if (!await CheckAsync(embeddedPreserved)) throw new InvalidOperationException("Origin clear affected embedded partition");
        SmokeTest.Report("audit: site clear removed localStorage, IndexedDB and CacheStorage; login cookie, adjacent port and embedded partition preserved=True");

        OpenSessionHistory();
        for (int i = 0; i < 50 && !PrivacyFlyout.IsOpen; i++) await Task.Delay(25);
        await Task.Delay(180);
        HistorySearchBox.Text = neighbor.Address;
        for (int i = 0; i < 40 && HistoryListView.Items.Count != 1; i++) await Task.Delay(25);
        if (HistoryListView.Items.Count != 1 || HistoryListView.Items[0] is not Models.HistorySessionItem match ||
            !match.Url.StartsWith(neighbor.Address + "/", StringComparison.Ordinal))
            throw new InvalidOperationException($"Search did not isolate history by URL: shown={HistoryListView.Items.Count}, expected={_sessionHistory.Count(h=>h.Url.StartsWith(neighbor.Address + "/", StringComparison.Ordinal))}, open={PrivacyFlyout.IsOpen}, section={PrivacySections.SelectedItem == HistorySection}");
        HistorySearchBox.Text = "nonmatching history fixture";
        for (int i = 0; i < 40 && HistoryListView.Items.Count != 0; i++) await Task.Delay(25);
        if (HistoryListView.Items.Count != 0 || HistoryEmptyText.Text != "没有匹配的浏览记录") throw new InvalidOperationException("History empty result feedback failed");
        HistorySearchBox.Text = "Partition fixture";
        for (int i = 0; i < 40 && HistoryListView.Items.Count != 2; i++) await Task.Delay(25);
        if (HistoryListView.Items.Count != 2) throw new InvalidOperationException("History title search did not return both visited paths");
        for (int i = 0; i < 40 && HistoryListView.ContainerFromIndex(0) is not Microsoft.UI.Xaml.Controls.ListViewItem { IsLoaded: true }; i++)
            await Task.Delay(25);
        if (HistoryListView.ContainerFromIndex(0) is not Microsoft.UI.Xaml.Controls.ListViewItem { IsLoaded: true } ||
            HistorySearchBox.FocusState == Microsoft.UI.Xaml.FocusState.Unfocused) throw new InvalidOperationException("History results or keyboard focus unavailable");
        await Task.Delay(220);
        await UiCapture.SaveAsync(PrivacyContent, "r161-session-history.png");
        PrivacySections.SelectedItem = StorageSection;
        await Task.Delay(200);
        await UiCapture.SaveAsync(PrivacyContent, "r161-site-storage.png");
        PrivacySections.SelectedItem = HistorySection;
        HistorySearchBox.Text = "Partition fixture";
        PrivacyFlyout.Hide();
        for (int i = 0; i < 50 && HistorySearchBox.Text.Length > 0; i++) await Task.Delay(25);
        if (HistorySearchBox.Text.Length != 0 || !ReferenceEquals(HistoryListView.ItemsSource, _sessionHistory))
            throw new InvalidOperationException("Closing session history retained its search term or snapshot");
        SmokeTest.Report("audit: history matches title and URL, reports empty results and discards the search when dismissed=True");

        var complete = new TaskCompletionSource<bool>();
        void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => complete.TrySetResult(args.IsSuccess);
        core.NavigationCompleted += Completed;
        try
        {
            core.Navigate("about:blank");
            if (!await complete.Task.WaitAsync(TimeSpan.FromSeconds(8)) || UrlTextBox.Text != "about:blank" ||
                BookmarkStarBtn.IsEnabled || CurrentTitle == "Partition fixture")
                throw new InvalidOperationException("Blank navigation left stale site identity in browser chrome");
        }
        finally { core.NavigationCompleted -= Completed; }
        SmokeTest.Report("audit: blank document replaces previous address and title and disables bookmarking=True");
    }
}
