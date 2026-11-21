using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using StealthWorkspace.Controls;
using StealthWorkspace.Pages;
using StealthWorkspace.Services;
using Windows.UI.ViewManagement;

namespace StealthWorkspace;

public sealed partial class MainWindow
{
    private async Task CapturePrivacyShieldsAsync()
    {
        if (!SmokeTest.ShieldVisualAudit || !AppPaths.HasDataRootOverride)
            throw new InvalidOperationException("An isolated shield fixture is required.");

        string fixture = Path.Combine(AppPaths.DataRoot, "shield-fixture.html");
        if (File.Exists(fixture))
        {
            var bookmarks = await BookmarkHtml.ReadAsync(fixture, default);
            if (bookmarks.Items.Count != 3 || bookmarks.Items.Any(item => !Uri.TryCreate(item.Url, UriKind.Absolute, out var url) || !url.IsLoopback))
                throw new InvalidDataException("The shield fixture must contain three synthetic loopback bookmarks.");
            var settings = SettingsService.Instance;
            if (settings.PersonalBookmarkSession is not { } session ||
                !settings.ImportBookmarkLibrary(bookmarks, session, default, out int links, out _) || links != 3)
                throw new IOException("Synthetic bookmark import failed.");
        }

        NavigateToPage(typeof(BrowserPage), null, "browser", force: true);
        BrowserTabControl? tab = null;
        for (int i = 0; i < 100 && tab == null; i++)
        {
            if (NavFrame.Content is BrowserPage page && page.SmokeSelectedTab is { IsLoaded: true } active) tab = active;
            else await Task.Delay(50);
        }
        if (tab?.FindName("AddressPrivacyShield") is not PrivacyShield addressShield ||
            tab.FindName("SiteShieldBtn") is not Button addressButton ||
            StatusShield.Visibility != Visibility.Visible || !StatusBadge.IsEnabled ||
            !AutomationProperties.GetName(StatusBadge).Contains("清理") || AutomationProperties.GetName(addressButton) != "站点隐私")
            throw new InvalidOperationException("Both privacy buttons must be present, enabled and named.");

        bool systemContrast = new AccessibilitySettings().HighContrast;
        SmokeTest.Report($"shield audit: operating system contrast mode={systemContrast}, scale={RootGrid.XamlRoot.RasterizationScale}");
        async Task Capture(string mode)
        {
            RootGrid.UpdateLayout();
            // Native button brushes animate across theme changes even without custom page motion.
            await Task.Delay(400);
            await UiCapture.SaveAsync(RootGrid, $"new-tab-{mode}.png");
            await UiCapture.SaveAsync(StatusBadge, $"title-entry-{mode}.png");
            await UiCapture.SaveAsync(addressButton, $"address-entry-{mode}.png");
            await UiCapture.SaveAsync(StatusShield, $"title-shield-{mode}.png");
            await UiCapture.SaveAsync(addressShield, $"address-shield-{mode}.png");
        }

        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            RootGrid.RequestedTheme = theme;
            await Capture(theme.ToString().ToLowerInvariant());
            if (!systemContrast)
                foreach (var shield in new[] { StatusShield, addressShield })
                {
                    byte[] pixels = await UiCapture.PixelsAsync(shield);
                    int cyan = 0, blue = 0;
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        if (pixels[i+3] < 220) continue;
                        int b = pixels[i], g = pixels[i+1], r = pixels[i+2];
                        if (g > 150 && g > r + 55 && b > 195) cyan++;
                        if (b > 120 && b > g + 70 && b > r + 90) blue++;
                    }
                    if (cyan < 3 || blue < 3) throw new InvalidOperationException($"Missing shield layers in {theme}: cyan={cyan}, blue={blue}.");
                    SmokeTest.Report($"shield layers {theme} {shield.ActualWidth} DIP: cyan={cyan}, blue={blue}");
                }
        }

        RootGrid.RequestedTheme = ElementTheme.Light;
        StatusShield.PreviewSystemContrast();
        addressShield.PreviewSystemContrast();
        await Capture(systemContrast ? "system-high-contrast" : "system-color-preview");
        await tab.CaptureShieldAccountsAsync();
        SmokeTest.Report("shield visual audit complete");
    }
}
