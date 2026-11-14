using Microsoft.Web.WebView2.Core;

namespace StealthWorkspace.Services;

internal static class BrowserSiteData
{
    internal static bool CookieBelongsToHost(string cookieDomain, string host)
    {
        // A leading dot denotes a domain cookie. A host-only cookie on the
        // parent does not belong to a child host, nor do substring lookalikes.
        bool domainCookie = cookieDomain.StartsWith('.');
        string domain = cookieDomain.TrimStart('.').TrimEnd('.');
        host = host.TrimEnd('.');
        return domain.Length != 0 && host.Length != 0 &&
            (host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
             (domainCookie && host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)));
    }

    internal static Task<int> DeleteCookiesForHostAsync(CoreWebView2 core, string host, Func<bool> canContinue) =>
        DeleteMatchingCookiesAsync(core, cookie => CookieBelongsToHost(cookie.Domain, host), canContinue);

    internal static async Task<int> DeleteMatchingCookiesAsync(CoreWebView2 core, Func<CoreWebView2Cookie, bool> matches, Func<bool> canContinue)
    {
        // Query the engine, not a lazily populated flyout collection. Fetch all
        // paths so cookies scoped to /account are cleared even from the homepage.
        var cookies = await core.CookieManager.GetCookiesAsync(string.Empty);
        if (!canContinue()) return 0;
        var targets = cookies.Where(matches)
            .Select(cookie => (cookie.Name, cookie.Domain, cookie.Path)).ToHashSet();
        foreach (var cookie in cookies)
        {
            if (!matches(cookie)) continue;
            core.CookieManager.DeleteCookie(cookie);
        }
        // DeleteCookie posts to the network process and has no completion event.
        // A same-tick GetCookiesAsync can still return the old snapshot. Confirm
        // the specific deletion before telling the user it has completed.
        for (int attempt = 0; attempt < 20 && targets.Count != 0; attempt++)
        {
            await Task.Delay(50);
            if (!canContinue()) return 0;
            var remaining = await core.CookieManager.GetCookiesAsync(string.Empty);
            if (!remaining.Any(cookie => targets.Contains((cookie.Name, cookie.Domain, cookie.Path)))) return targets.Count;
        }
        if (targets.Count != 0) throw new InvalidOperationException("Cookie deletion not confirmed");
        return 0;
    }
}
