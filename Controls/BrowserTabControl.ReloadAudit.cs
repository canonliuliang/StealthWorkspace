using Microsoft.Web.WebView2.Core;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    internal async Task CheckHardReloadAuditAsync()
    {
        await using var fixture = new BrowserHttpFixture();
        await NavigateAuditAsync(fixture.Address + "/cache-page");
        await EvaluateAuditAsync("document.cookie='reload-fixture=keep;path=/';sessionStorage.setItem('reload-fixture','keep')");
        await NavigateAuditAsync(fixture.Address + "/page");
        await NavigateAuditAsync(fixture.Address + "/cache-page");
        if (fixture.CountRequests("/cached-script") != 1)
            throw new InvalidOperationException("Cache fixture was not actually cached");
        var core = _hookedCore!;
        var complete = new TaskCompletionSource<bool>();
        void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => complete.TrySetResult(args.IsSuccess);
        core.NavigationCompleted += Completed;
        try
        {
            await ReloadIgnoringCacheAsync();
            if (!await complete.Task.WaitAsync(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Hard reload navigation failed");
            bool ok = fixture.CountRequests("/cached-script") == 2 &&
                await EvaluateAuditAsync("window.fixtureVersion===2 && document.cookie.includes('reload-fixture=keep') && sessionStorage.getItem('reload-fixture')==='keep'") == "true";
            SmokeTest.Report("audit: bypass-cache reload fetches new resource and preserves cookie/sessionStorage=" + ok);
            if (!ok) throw new InvalidOperationException("Hard reload did not preserve the browsing session");
        }
        finally { core.NavigationCompleted -= Completed; }

        await EvaluateAuditAsync("""
            window.offlineFixtureReady = false;
            localStorage.setItem('r16-draft','keep');
            caches.open('r16-offline').then(cache => cache.put('/offline-note', new Response('offline-text')))
                .then(() => window.offlineFixtureReady = true);
            """);
        for (int i = 0; i < 50 && await EvaluateAuditAsync("window.offlineFixtureReady") != "true"; i++) await Task.Delay(50);
        if (await EvaluateAuditAsync("window.offlineFixtureReady") != "true") throw new InvalidOperationException("Offline fixture was not stored");
        await BrowserSiteData.ClearHttpCacheAsync(core);
        int before = fixture.CountRequests("/cached-script");
        await NavigateAuditAsync(fixture.Address + "/page");
        await NavigateAuditAsync(fixture.Address + "/cache-page");
        await EvaluateAuditAsync("""
            window.offlineFixturePreserved = false;
            caches.match('/offline-note').then(response => response?.text())
                .then(text => window.offlineFixturePreserved = text === 'offline-text'
                    && localStorage.getItem('r16-draft') === 'keep' && document.cookie.includes('reload-fixture=keep'));
            """);
        for (int i = 0; i < 50 && await EvaluateAuditAsync("window.offlineFixturePreserved") != "true"; i++) await Task.Delay(50);
        if (fixture.CountRequests("/cached-script") <= before || await EvaluateAuditAsync("window.offlineFixturePreserved") != "true")
            throw new InvalidOperationException("HTTP cache clearing must fetch a fresh script and preserve offline data, drafts and cookies");
        SmokeTest.Report("audit: HTTP cache clear refetches script, preserves CacheStorage response, local draft and login cookie=True");
    }
}
