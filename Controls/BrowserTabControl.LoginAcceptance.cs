using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    internal async Task VerifyLoginFixtureAsync(bool secondSite, string action, bool authenticated, bool hasStorage)
    {
        Uri page = LoginRetentionAcceptance.Page(secondSite, action);
        NavigateToText(page.AbsoluteUri);
        for (int i = 0; i < 300; i++)
        {
            await Task.Delay(60);
            if (!_documentReady || _hookedCore is not { } core || core.Source != page.AbsoluteUri) continue;
            // Only the randomly named loopback page can supply this attestation.
            string result = await core.ExecuteScriptAsync("document.documentElement.dataset.verdict || ''");
            string? value = JsonSerializer.Deserialize<string>(result);
            if (string.IsNullOrEmpty(value)) continue;
            string expected = string.Join("|", LoginRetentionAcceptance.RunId, authenticated ? "yes" : "no",
                hasStorage ? "yes" : "no", hasStorage ? "yes" : "no", "private");
            if (value != expected)
                throw new InvalidOperationException($"Synthetic site {(secondSite ? "B" : "A")} {action}: expected {expected}, got {value}.");
            string profile = BrowserProfileManager.ActiveSessionDirectory ?? throw new InvalidOperationException("Missing synthetic profile.");
            File.AppendAllLines(SmokeTest.OutputPath + ".profiles", [profile]);
            SmokeTest.Report($"login fixture: site {(secondSite ? "B" : "A")} {action}, signed in={authenticated}, localStorage and IndexedDB={hasStorage}, HttpOnly hidden");
            if (!BrowserSessionHistory.Items.Any(entry => entry.Url == page.AbsoluteUri))
                throw new InvalidOperationException("The synthetic visit was not recorded in this unlocked session.");
            return;
        }
        throw new TimeoutException("The loopback login page did not deliver its fixture result.");
    }

    internal async Task ForgetLoginFixtureSiteAsync()
    {
        if (_hookedCore is not { } core || !Uri.TryCreate(core.Source, UriKind.Absolute, out var page) ||
            page.Host != "127.0.0.1" || !page.AbsolutePath.StartsWith("/" + LoginRetentionAcceptance.RunId + "/", StringComparison.Ordinal))
            throw new InvalidOperationException("Only the first synthetic site may be cleared.");
        await ChooseAccountActionAsync(() => ForgetSiteButton, confirm: true);
        for (int i = 0; i < 160; i++)
        {
            await Task.Delay(50);
            if (!VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Any(p => FindFixtureButton(p.Child, "PrimaryButton") != null)
                && !(await core.CookieManager.GetCookiesAsync(page.GetLeftPart(UriPartial.Authority))).Any(c => c.Name == "fixture_session")
                && await core.ExecuteScriptAsync("localStorage.getItem('fixture_value') === null") == "true")
                return;
        }
        throw new InvalidOperationException("The confirmed site action did not remove the fixture cookie.");
    }

    internal async Task ClearLoginFixturesFromBlankTabAsync()
    {
        if (_hookedCore != null || BrowserProfileManager.ActiveSessionDirectory != null)
            throw new InvalidOperationException("The initial blank tab already acquired a browser profile.");
        await ChooseAccountActionAsync(() => ClearAllAccountsBtn, confirm: false);
        if (_hookedCore != null || BrowserProfileManager.ActiveSessionDirectory != null)
            throw new InvalidOperationException("Cancelling acquires an unnecessary WebView2 profile.");
        await ChooseAccountActionAsync(() => ClearAllAccountsBtn, confirm: true);
        for (int i = 0; i < 200; i++)
        {
            await Task.Delay(60);
            if (_hubStatus == "已清除所有网站的登录和离线数据")
            {
                SmokeTest.Report("login fixture: blank tab cancelled without creating an engine, then confirmed removal for all sites");
                return;
            }
        }
        throw new InvalidOperationException("Clearing all accounts from the initial blank tab did not complete.");
    }

    private async Task ChooseAccountActionAsync(Func<Button> getButton, bool confirm)
    {
        if (!PrivacyFlyout.IsOpen) MediaAcceptance.Invoke(SiteShieldBtn);
        await Task.Delay(200);
        PrivacySections.SelectedItem = CookiesSection;
        await Task.Delay(180);
        Button action = getButton() ?? throw new InvalidOperationException("The Cookie panel was not materialized.");
        if (!PrivacyFlyout.IsOpen || !action.IsLoaded || !action.IsEnabled || action.ActualHeight <= 0)
            throw new InvalidOperationException("The account action was unavailable in the Cookie panel: " + action.Name);
        MediaAcceptance.Invoke(action);
        string name = confirm ? "PrimaryButton" : "CloseButton";
        for (int i = 0; i < 120; i++)
        {
            await Task.Delay(50);
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
                if (FindFixtureButton(popup.Child, name) is { } answer)
                {
                    MediaAcceptance.Invoke(answer);
                    for (int j = 0; j < 100 && VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Any(p => FindFixtureButton(p.Child, name) != null); j++)
                        await Task.Delay(50);
                    return;
                }
        }
        throw new InvalidOperationException("The native confirmation did not offer " + name + ".");
    }

    internal static Button? FindFixtureButton(Microsoft.UI.Xaml.DependencyObject? node, string name)
    {
        if (node == null) return null;
        if (node is Button { IsEnabled: true } b && b.Name == name) return b;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindFixtureButton(VisualTreeHelper.GetChild(node, i), name) is { } child) return child;
        return null;
    }
}
