using Microsoft.UI.Xaml.Controls;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    internal async Task CaptureShieldAccountsAsync()
    {
        await using var site = new BrowserHttpFixture();
        await NavigateAuditAsync(site.Address + "/page");
        MediaAcceptance.Invoke(SiteShieldBtn);
        try
        {
            for (int i = 0; i < 100 && PrivacyContent is not { IsLoaded: true }; i++) await Task.Delay(40);
            if (!PrivacyFlyout.IsOpen || PrivacyContent is not { IsLoaded: true }) throw new InvalidOperationException("The privacy menu did not open.");
            PrivacySections.SelectedItem = CookiesSection;
            Button[] buttons = [SiteCookiesOnlyButton, AllCookiesOnlyButton, ForgetSiteButton, ClearAllAccountsBtn];
            for (int i = 0; i < 100 && !buttons.All(button => button.IsLoaded && button.IsEnabled && button.ActualHeight > 0); i++)
                await Task.Delay(40);
            PrivacyContent.UpdateLayout();
            foreach (var button in buttons)
            {
                var origin = button.TransformToVisual(PanelCookie).TransformPoint(default);
                if (!button.IsEnabled || origin.X < 0 || origin.Y < 0 || button.ActualHeight <= 0 ||
                    origin.X + button.ActualWidth > PanelCookie.ActualWidth + 1 ||
                    origin.Y + button.ActualHeight > PanelCookie.ActualHeight + 1)
                    throw new InvalidOperationException("Unavailable or clipped site account action: " + button.Name);
            }
            await UiCapture.SaveAsync(PrivacyContent, "site-account-panel-light.png");
            SmokeTest.Report("shield audit: all four site account actions visible and enabled on isolated loopback page.");
        }
        finally { PrivacyFlyout.Hide(); }
    }
}
