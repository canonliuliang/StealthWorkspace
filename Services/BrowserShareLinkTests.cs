using Windows.ApplicationModel.DataTransfer;

namespace StealthWorkspace.Services;

internal static class BrowserShareLinkTests
{
    internal static async Task RunAsync(Action<bool, string, string> report)
    {
        const string original = "https://example.test/a%2Fb?ref=home&UTM_Source=one&id=2&id=3&gclid=x&bare&keep=%2f+%20#part%2F1";
        const string expected = "https://example.test/a%2Fb?ref=home&id=2&id=3&bare&keep=%2f+%20#part%2F1";
        report(BrowserShareLink.Prepare(original, out var shared) == ShareLinkResult.Cleaned && shared == expected,
            "Clean-link copy preserves original encoding, business fields, duplicates, ordering and fragment", "");
        const string mixed = "https://example.test/path?a=1&&utm_source=one;id=2&ref=other&utm_inventory=value#part?fbclid=x";
        report(BrowserShareLink.Prepare(mixed, out shared) == ShareLinkResult.Unchanged && shared == mixed &&
            BrowserShareLink.Prepare("https://example.test/?%75tm_source=one&fbclid=two#bookmark", out shared) == ShareLinkResult.Cleaned &&
            shared == "https://example.test/#bookmark", "Only named query fields are removed; ambiguous delimiters and fragments survive", "");
        bool untouched = true;
        string[] privateLinks =
        [
            "https://demo:fixture@example.test/document", "https://accounts.example.test/page",
            "https://example.test/login?utm_source=x", "https://example.test/checkout/123",
            "https://example.test/file?%73ignature=test&utm_source=x", "https://example.test/file?X-Amz-Credential=test",
            "https://example.test/file?x-goog-signature=test", "https://example.test/?foo=1;sig=t",
            "https://example.test/page?code=test", "https://example.test/#/callback?access_token=test",
            "https://example.test/#id_token=test", "https://example.test/#/login"
        ];
        bool sensitive = privateLinks.All(link => BrowserShareLink.Copy(link, (_, _) => { untouched = false; return true; }) == ShareLinkResult.Sensitive);
        bool unavailable = new string?[] { null, "", "about:blank", "file:///fixture", "a search", "https://example.test/ a" }
            .All(link => BrowserShareLink.Copy(link, (_, _) => { untouched = false; return true; }) == ShareLinkResult.Unavailable);
        report(sensitive && unavailable && untouched, "Sensitive, signed, login, checkout and non-Web links leave the clipboard untouched", "");
        DataPackage? observed = null;
        bool privateOptions = false;
        var status = BrowserShareLink.Copy(original, (package, options) =>
        {
            observed = package;
            privateOptions = !options.IsAllowedInHistory && !options.IsRoamable;
            return true;
        });
        var view = observed?.GetView();
        report(status == ShareLinkResult.Cleaned && privateOptions && view?.Contains(StandardDataFormats.Text) == true &&
            await view.GetTextAsync() == expected, "Share command builds a text DataPackage with Windows history and cloud roaming disabled", "");
        report(BrowserShareLink.Copy("https://example.test/index", (_, _) => false) == ShareLinkResult.ClipboardBusy,
            "A busy clipboard reports copy failure", "");
    }
}
