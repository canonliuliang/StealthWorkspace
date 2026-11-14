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
    }
}
