using Microsoft.UI.Xaml;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    internal async Task CheckPrivacyExperienceAsync()
    {
        var cfg = SettingsService.Instance.Data;
        var original = (cfg.BlockCamera, cfg.BlockMicrophone, cfg.BlockGeolocation, cfg.BlockNotifications);
        EnsurePrivacyContent();
        PrivacyFlyout.ShowAt(SiteShieldBtn);
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            PrivacyContent.RequestedTheme = theme;
            await Task.Delay(350);
            SetActiveTab(TabFirewallBtn, PanelFirewall);
            await Task.Delay(220);
            var toggles = new[] { CameraBlockToggle, MicBlockToggle, GeoBlockToggle, NotifyBlockToggle };
            bool aligned = toggles.All(toggle => Math.Abs(toggle.ActualWidth - 48) < 0.5 && toggle.IsOn && toggle.TransformToVisual(PrivacyContent).TransformPoint(default).X + toggle.ActualWidth <= PrivacyContent.ActualWidth);
            if (!aligned) throw new InvalidOperationException("Privacy toggles clipped or not aligned");
            await UiCapture.SaveAsync(PrivacyContent, $"r10-privacy-{theme.ToString().ToLowerInvariant()}.png");
            SetActiveTab(TabStorageBtn, PanelStorage);
            await Task.Delay(180);
            SetActiveTab(TabHistoryBtn, PanelHistory);
            await Task.Delay(180);
            SetActiveTab(TabFirewallBtn, PanelFirewall);
            SmokeTest.Report("privacy experience: aligned controls and panel transitions in " + theme);
        }
        PrivacyFlyout.Hide();
        PrivacyContent.RequestedTheme = ElementTheme.Default;
        if (original != (cfg.BlockCamera, cfg.BlockMicrophone, cfg.BlockGeolocation, cfg.BlockNotifications))
            throw new InvalidOperationException("Privacy policy changed while viewing panel");
    }
}
