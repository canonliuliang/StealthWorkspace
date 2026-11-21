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
                    int ink = 0, clear = 0;
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        if (pixels[i+3] < 16) clear++;
                        else if (pixels[i+3] > 72) ink++;
                    }
                    if (!shield.Compact || ink < 12 || clear < pixels.Length / 8)
                        throw new InvalidOperationException($"Toolbar shield lost its outline in {theme}.");
                }
        }

        RootGrid.RequestedTheme = ElementTheme.Light;
        StatusShield.PreviewSystemContrast();
        addressShield.PreviewSystemContrast();
        await Capture(systemContrast ? "system-high-contrast" : "system-color-preview");
        if (!File.Exists(fixture)) await CheckBookmarkPaneAsync(tab);
        await tab.CaptureShieldAccountsAsync();
        SmokeTest.Report("shield visual audit complete");
    }

    private async Task CheckBookmarkPaneAsync(BrowserTabControl tab)
    {
        MediaAcceptance.Invoke((Button)tab.FindName("ManageBarBookmarks"));
        await Task.Delay(350);

        var manager = tab.ActiveBarEditor ?? throw new InvalidOperationException("Bookmark bar editor did not open.");
        var library = SettingsService.Instance;
        foreach (string folder in new[] { "视频", "图片" })
        {
            MediaAcceptance.Invoke((Button)manager.FindName("Create"));
            await Task.Delay(80);
            ((TextBox)manager.FindName("EntryName")).Text = folder;
            await SaveAsync();
        }
        var video = library.GetBookmarkLibrary().Folders.Single(f => f.Name == "视频");
        MediaAcceptance.Invoke((Button)manager.FindName("AddLink"));
        await Task.Delay(80);
        ((TextBox)manager.FindName("EntryName")).Text = "影片详情";
        ((TextBox)manager.FindName("Address")).Text = "https://example.com/watch/42";
        var dest = (ComboBox)manager.FindName("ParentFolder");
        dest.SelectedItem = dest.Items.OfType<BookmarkManager.Destination>().Single(d => d.Id == video.Id);
        await SaveAsync();
        if (!library.GetBookmarkLibrary().Items.Any(i => i.FolderId == video.Id && i.Url == "https://example.com/watch/42"))
            throw new InvalidOperationException("The bookmark was not saved in the selected folder.");
        var list = (ListView)manager.FindName("Rows");
        await Task.Delay(550);
        if (list.Items.Count != 2 || list.ContainerFromIndex(0) is not ListViewItem { ActualHeight: > 0 })
            throw new InvalidOperationException($"Folder list is not visible ({list.Items.Count} rows, {list.ActualHeight} DIP).");
        await UiCapture.SaveAsync(manager, "bar-editor.png");
        manager.ClosePane?.Invoke();
        await Task.Delay(450);
        var bar = (Grid)tab.FindName("BookmarkBar");
        var page = (Grid)tab.FindName("WebContentArea");
        var entries = (StackPanel)tab.FindName("BookmarkBarEntries");
        double left = bar.TransformToVisual(page).TransformPoint(default).X;
        if (Math.Abs(left - page.ActualWidth) > 1 || entries.Orientation != Orientation.Vertical || entries.Children.Count < 2)
            throw new InvalidOperationException("Bookmark folders must be stacked to the right of the page.");
        await UiCapture.SaveAsync(RootGrid, "bookmark-bar.png");
        if (tab.ActiveBarEditor != null) throw new InvalidOperationException("Closing the pane retained the manager.");
        async Task SaveAsync()
        {
            MediaAcceptance.Invoke((Button)manager.FindName("Save"));
            var editor = (StackPanel)manager.FindName("Editor");
            for (int i = 0; i < 100 && editor.Visibility == Visibility.Visible; i++) await Task.Delay(40);
            if (editor.Visibility == Visibility.Visible) throw new InvalidOperationException("Bookmark editor failed to save.");
        }
    }
}
