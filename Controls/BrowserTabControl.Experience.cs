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
            PrivacySections.SelectedItem = PermissionsSection;
            await Task.Delay(220);
            var toggles = new[] { CameraBlockToggle, MicBlockToggle, GeoBlockToggle, NotifyBlockToggle };
            bool aligned = toggles.All(toggle => Math.Abs(toggle.ActualWidth - 48) < 0.5 && toggle.IsOn && toggle.TransformToVisual(PrivacyContent).TransformPoint(default).X + toggle.ActualWidth <= PrivacyContent.ActualWidth);
            if (!aligned) throw new InvalidOperationException("Privacy toggles clipped or not aligned");
            await UiCapture.SaveAsync(PrivacyContent, $"r10-privacy-{theme.ToString().ToLowerInvariant()}.png");
            PrivacySections.SelectedItem = StorageSection;
            await Task.Delay(180);
            PrivacySections.SelectedItem = HistorySection;
            PrivacySections.SelectedItem = CookiesSection;
            PrivacySections.SelectedItem = PermissionsSection;
            if (PanelFirewall.Visibility != Visibility.Visible || PrivacyPanels.Children.Count(p => p.Visibility == Visibility.Visible) != 1)
                throw new InvalidOperationException("Rapid section changes left the wrong privacy panel visible");
            SmokeTest.Report("privacy experience: aligned controls and rapid panel changes in " + theme);
        }
        PrivacyFlyout.Hide();
        PrivacyContent.RequestedTheme = ElementTheme.Default;
        if (original != (cfg.BlockCamera, cfg.BlockMicrophone, cfg.BlockGeolocation, cfg.BlockNotifications))
            throw new InvalidOperationException("Privacy policy changed while viewing panel");

        // A transient load indicator must never change the native controller's viewport.
        UpdateLayout();
        var originalVisibility = BrowserProgressBar.Visibility;
        double height = BrowserHost.ActualHeight;
        BrowserProgressBar.Visibility = originalVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        UpdateLayout();
        bool stable = height > 0 && Math.Abs(BrowserHost.ActualHeight - height) < 0.01;
        BrowserProgressBar.Visibility = originalVisibility;
        if (!stable) throw new InvalidOperationException("Browser viewport resized when the load indicator changed");

        ShowDownloadToast("\uE896", "正在下载", "测试文件");
        DismissDownloadToast_Click(this, new RoutedEventArgs());
        ShowDownloadToast("\uE73E", "下载完成", "测试文件");
        await Task.Delay(350);
        if (DownloadToast.Visibility != Visibility.Visible || DownloadToastTitle.Text != "下载完成" || !DownloadToast.IsHitTestVisible)
            throw new InvalidOperationException("An interrupted dismissal hid the new download notification");
        DismissDownloadToast_Click(this, new RoutedEventArgs());
        await Task.Delay(350);
        if (DownloadToast.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Download notification did not close");
        SmokeTest.Report("browser motion: stable native viewport; notification dismiss/reopen/dismiss passed");
    }
}
