using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
        var opener = (Button)tab.FindName("ManageBarBookmarks");
        var bar = (Grid)tab.FindName("BookmarkBar");
        var page = (Grid)tab.FindName("WebContentArea");
        await WaitForAsync(() => opener.IsLoaded && opener.ActualWidth > 0 && tab.ActiveBarEditor == null && Math.Abs(bar.ActualWidth - 40) <= 1,
            "The empty library must leave only the right edge button visible.");
        double collapsedWidth = page.ActualWidth;
        await UiCapture.SaveAsync(RootGrid, "bookmark-bar-collapsed.png");
        MediaAcceptance.Invoke(opener);
        await WaitForAsync(() => tab.ActiveBarEditor is { IsLoaded: true } && bar.ActualWidth >= 264, "The right bookmark rail did not open.");
        var manager = tab.ActiveBarEditor!;
        var list = (ListView)manager.FindName("Rows");
        var filter = (TextBox)manager.FindName("Filter");
        var editor = (StackPanel)manager.FindName("Editor");
        double left = bar.TransformToVisual(page).TransformPoint(default).X;
        double right = left + bar.ActualWidth;
        bool overlay = Grid.GetColumnSpan(page) == 2;
        double rightEdgeError = overlay ? right - page.ActualWidth - 40 : left - page.ActualWidth;
        if (left <= 0 || Math.Abs(rightEdgeError) > 1 || manager.ActualWidth > bar.ActualWidth + 1 ||
            (overlay && Math.Abs(page.ActualWidth - collapsedWidth) > 1))
            throw new InvalidOperationException("Bookmark rail is not aligned to the right of the document.");
        SmokeTest.Report($"bookmark rail geometry: collapsed page={collapsedWidth:F0}, expanded page={page.ActualWidth:F0}, rail={bar.ActualWidth:F0} DIP, overlay={overlay}");
        var library = SettingsService.Instance;
        await using var site = new BrowserHttpFixture();
        foreach (string folder in new[] { "视频", "图片" })
        {
            await BeginEditAsync("Create");
            ((TextBox)manager.FindName("EntryName")).Text = folder;
            await SaveAsync();
        }
        var video = library.GetBookmarkLibrary().Folders.Single(f => f.Name == "视频");
        await BeginEditAsync("AddLink");
        ((TextBox)manager.FindName("EntryName")).Text = "影片详情";
        ((TextBox)manager.FindName("Address")).Text = site.Address + "/featured";
        var dest = (ComboBox)manager.FindName("ParentFolder");
        dest.SelectedItem = dest.Items.OfType<BookmarkManager.Destination>().Single(d => d.Id == video.Id);
        await SaveAsync();
        if (!library.GetBookmarkLibrary().Items.Any(i => i.FolderId == video.Id && i.Url == site.Address + "/featured"))
            throw new InvalidOperationException("The bookmark was not saved in the selected folder.");

        string[] categories = ["新闻", "技术", "摄影", "旅行", "科学", "设计", "书籍", "音乐", "电影", "课程", "参考资料", "画展", "食谱", "运动", "阅读清单"];
        var folders = categories.Select((name, index) => new BookmarkFolder("f" + index, "", name, index)).ToList();
        folders.Add(new("video", "", video.Name));
        folders.Add(new("watch", "video", "稍后观看", 0));
        var episodes = Enumerable.Range(1, 25).Select(number => new BookmarkItem
        {
            Title = $"城市漫步 · 第 {number:D2} 集", Url = $"{site.Address}/episodes/{number}",
            FolderId = "watch", Position = number
        }).ToList();
        if (library.PersonalBookmarkSession is not { } session ||
            !library.ImportBookmarkLibrary(new(episodes, folders, 0), session, default, out int added, out int addedFolders) ||
            added != episodes.Count || addedFolders != categories.Length + 1)
            throw new IOException("The multi-folder audit library was not imported.");
        await WaitForAsync(() => list.Items.Count == categories.Length + 2, "The rail did not show all imported root folders.");
        var rootVideo = Row("视频");
        if (list.Items.IndexOf(rootVideo) < 12)
            throw new InvalidOperationException("Fixture did not exercise the former 12-item root limit.");
        await InvokeRowAsync(rootVideo);
        await WaitForAsync(() => manager.CurrentFolderId == video.Id && list.Items.Count == 2, "The late root folder did not open from its row.");
        await InvokeRowAsync(Row("稍后观看"));
        await WaitForAsync(() => list.Items.Count == episodes.Count, "The nested video directory is missing links.");
        string nestedFolder = manager.CurrentFolderId;
        list.UpdateLayout();
        await WaitForAsync(() => list.ContainerFromIndex(0) is ListViewItem row &&
            ContainsTitle(row, episodes[0].Title), "The visible first row did not switch to the new directory.");
        await UiCapture.SaveAsync(RootGrid, "bookmark-bar.png");
        await UiCapture.SaveAsync(manager, "bar-editor.png");
        filter.Text = "影片详情";
        await WaitForAsync(() => list.Items.Count == 1 && list.Items[0] is BookmarkManagerEntry { Title: "影片详情", ParentPath: "视频" },
            "Global search did not find a link outside the current subfolder.");
        MediaAcceptance.Invoke((Button)manager.FindName("Up"));
        await WaitForAsync(() => filter.Text.Length == 0 && manager.CurrentFolderId == nestedFolder && list.Items.Count == 25,
            "Leaving search did not restore the current directory.");

        await OpenEpisodeAsync(23);
        var anchor = Row(episodes[22].Title);
        var realized = (ListViewItem)list.ContainerFromItem(anchor);
        double rowY = realized.TransformToVisual(list).TransformPoint(default).Y;
        MediaAcceptance.Invoke((Button)manager.FindName("CloseRail"));
        await WaitForAsync(() => tab.ActiveBarEditor == null && opener.Visibility == Visibility.Visible && Math.Abs(bar.ActualWidth - 40) <= 1,
            "The rail could not be collapsed after opening a link.");
        MediaAcceptance.Invoke(opener);
        await WaitForAsync(() => ReferenceEquals(manager, tab.ActiveBarEditor) && manager.CurrentFolderId == nestedFolder && list.ActualHeight > 0,
            "Reopening bookmarks did not restore the same rail and directory.");
        list.UpdateLayout();
        if (list.ContainerFromItem(anchor) is not ListViewItem restored || Math.Abs(restored.TransformToVisual(list).TransformPoint(default).Y - rowY) > 2)
            throw new InvalidOperationException("Reopening the rail lost the scroll position.");
        await OpenEpisodeAsync(24);
        await UiCapture.SaveAsync(RootGrid, "bookmark-reading.png");
        if (Environment.GetEnvironmentVariable("STEALTH_TEST_CAPTURE_BOOKMARK_WINDOW") == "1")
        {
            string captured = Path.Combine(Path.GetDirectoryName(SmokeTest.OutputPath!)!, "bookmark-window.ready");
            await WaitForAsync(() => File.Exists(captured), "The native window screenshot was not captured.");
        }
        var browser = (BrowserPage)NavFrame.Content;
        var tabs = (TabView)browser.FindName("BrowserTabView");
        var firstTab = (TabViewItem)tabs.SelectedItem;
        var addTab = FindAddButton(tabs) ?? throw new InvalidOperationException("The tab strip's new-tab button is missing.");
        MediaAcceptance.Invoke(addTab);
        await WaitForAsync(() => browser.SmokeSelectedTab is { } other && other != tab && other.ActiveBarEditor?.CurrentFolderId == nestedFolder,
            "A new tab in the same session did not open the shared bookmark directory.");
        var secondRail = browser.SmokeSelectedTab!.ActiveBarEditor!;
        MediaAcceptance.Invoke((Button)secondRail.FindName("Up"));
        await WaitForAsync(() => secondRail.CurrentFolderId == video.Id, "The second tab could not navigate back to the video folder.");
        tabs.SelectedItem = firstTab;
        await WaitForAsync(() => browser.SmokeSelectedTab == tab && tab.ActiveBarEditor?.CurrentFolderId == video.Id && list.Items.Count == 2,
            "Switching to the first tab lost the shared bookmark directory.");
        MediaAcceptance.Invoke((Button)manager.FindName("Up"));
        await WaitForAsync(() => manager.CurrentFolderId.Length == 0 && list.Items.Count == categories.Length + 2, "Back did not return to the bookmark root.");
        SmokeTest.Report($"bookmark rail: {categories.Length + 2} root folders, {episodes.Count} nested links, search found a link in the parent folder, two distinct loopback documents loaded, folder/scroll survived collapse, and current folder was shared between two tabs.");

        static bool ContainsTitle(DependencyObject element, string title)
        {
            if (element is TextBlock text && text.Text == title) return true;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                if (ContainsTitle(VisualTreeHelper.GetChild(element, i), title)) return true;
            return false;
        }

        static Button? FindAddButton(DependencyObject element)
        {
            if (element is Button { Name: "AddButton" } button) return button;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                if (FindAddButton(VisualTreeHelper.GetChild(element, i)) is { } child) return child;
            return null;
        }

        BookmarkManagerEntry Row(string title) => list.Items.OfType<BookmarkManagerEntry>().Single(entry => entry.Title == title);

        async Task InvokeRowAsync(BookmarkManagerEntry entry)
        {
            list.ScrollIntoView(entry);
            list.UpdateLayout();
            await WaitForAsync(() => list.ContainerFromItem(entry) is ListViewItem { ActualHeight: > 0 }, "List could not realize " + entry.Title);
            var container = (ListViewItem)list.ContainerFromItem(entry);
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(container);
            if (peer?.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new InvalidOperationException("No native invoke action for bookmark row " + entry.Title);
            invoke.Invoke();
        }

        async Task OpenEpisodeAsync(int number)
        {
            var episode = episodes[number - 1];
            string path = new Uri(episode.Url).AbsolutePath;
            var entry = Row(episode.Title);
            if (list.Items.IndexOf(entry) < 20) throw new InvalidOperationException("Fixture did not exercise the former 20-link limit.");
            await InvokeRowAsync(entry);
            await WaitForAsync(() => tab.CurrentUrl == episode.Url && site.CountRequests(path) > 0, "Bookmark row did not navigate to " + path);
            for (int i = 0; i < 200; i++)
            {
                if (await tab.EvaluateAuditAsync("location.pathname") == $"\"{path}\"")
                {
                    if (tab.ActiveBarEditor != manager || manager.CurrentFolderId != nestedFolder)
                        throw new InvalidOperationException("Opening a link dismissed the rail or reset the folder.");
                    return;
                }
                await Task.Delay(40);
            }
            throw new InvalidOperationException("The browser did not commit the bookmarked loopback document at " + path);
        }

        async Task BeginEditAsync(string button)
        {
            MediaAcceptance.Invoke((Button)manager.FindName(button));
            await WaitForAsync(() => editor.Visibility == Visibility.Visible, "The inline editor did not open from " + button);
        }

        async Task SaveAsync()
        {
            MediaAcceptance.Invoke((Button)manager.FindName("Save"));
            await WaitForAsync(() => editor.Visibility == Visibility.Collapsed && manager.IsEnabled, "Bookmark editor failed to save.");
        }

        static async Task WaitForAsync(Func<bool> ready, string failure)
        {
            for (int i = 0; i < 300; i++)
            {
                if (ready()) return;
                await Task.Delay(40);
            }
            throw new InvalidOperationException(failure);
        }
    }
}
