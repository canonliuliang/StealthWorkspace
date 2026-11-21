using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    internal async Task CheckMenuActionsAuditAsync()
    {
        if (!AppPaths.HasDataRootOverride || !SmokeTest.BrowserAudit) throw new InvalidOperationException("An isolated browser audit is required");
        static async Task WaitAsync(Func<bool> reached, string label, int timeoutMs = 5000)
        {
            for (int i = 0; i < timeoutMs / 25 && !reached(); i++) await Task.Delay(25);
            if (!reached()) throw new InvalidOperationException(label);
            SmokeTest.Report("menu UI: " + label);
        }
        static void Click(Button button)
        {
            if (!button.IsLoaded || !button.IsEnabled || button.Visibility != Visibility.Visible) throw new InvalidOperationException("Unavailable button: " + button.Name);
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        }
        async Task MenuAsync()
        {
            // The button toggles its flyout. Let the previous command's dismiss
            // animation finish before testing a new click on that same button.
            if (MoreActionsFlyout.IsOpen) MoreActionsFlyout.Hide();
            for (int i = 0; i < 40 && FolderMenuButton?.IsLoaded == true; i++) await Task.Delay(20);
            Click(BrowserMenuBtn);
            await WaitAsync(() => MoreActionsFlyout.IsOpen && FolderMenuButton?.IsLoaded == true, "opened toolbar menu");
            await Task.Delay(100);
        }

        await using var fixture = new BrowserHttpFixture();
        if (!Environment.GetCommandLineArgs().Contains("--folder-locations-only"))
        {
            await MenuAsync();
            if (PageActionsGroup.Visibility != Visibility.Visible || PageActionsHint.Visibility != Visibility.Visible ||
                !OpenInEdgeBtn.IsEnabled || FindMenuButton.IsEnabled || PageImagesBtn.IsEnabled || CopyCleanLinkBtn.IsEnabled ||
                HardReloadMenuButton.IsEnabled || AudioMuteBtn.IsEnabled || HasBrowserControl)
                throw new InvalidOperationException("New tab lost its Edge shortcut or displayed incorrect page actions");
            if (Environment.GetCommandLineArgs().Contains("--verify-edge-inprivate")) await VerifyEdgeMenuAsync(OpenInEdgeBtn, null);
            Click(ZoomMenuPlus);
            await WaitAsync(() => StartPageScroller.ZoomFactor > 1.09 && Equals(ZoomResetBtn.Content, "110%"), "new-tab plus enlarged the native start surface");
            Click(ZoomMenuMinus);
            await WaitAsync(() => Math.Abs(StartPageScroller.ZoomFactor - 1) < .01, "new-tab minus returned to 100%");
            Click(ZoomMenuMinus);
            await WaitAsync(() => StartPageScroller.ZoomFactor < .91, "new-tab minus reduced the start surface");
            Click(ZoomResetBtn);
            await WaitAsync(() => Math.Abs(StartPageScroller.ZoomFactor - 1) < .01, "new-tab reset restored its layout");
            MoreActionsFlyout.Hide();
            await WaitAsync(() => !MoreActionsFlyout.IsOpen, "new-tab menu closed");


            await NavigateAuditAsync(fixture.Address + "/page");
            await MenuAsync();
            if (PageActionsHint.Visibility != Visibility.Collapsed || !OpenInEdgeBtn.IsEnabled || !FindMenuButton.IsEnabled ||
                !PageImagesBtn.IsEnabled || !HardReloadMenuButton.IsEnabled || !AudioMuteBtn.IsEnabled || !CopyCleanLinkBtn.IsEnabled)
                throw new InvalidOperationException("Website commands missing");
            if (Environment.GetCommandLineArgs().Contains("--verify-edge-inprivate"))
            {
                int beforeEdge = fixture.CountRequests("/page");
                await VerifyEdgeMenuAsync(OpenInEdgeBtn, fixture.Address + "/page");
                if (!CleanerAccess.IsRestricted)
                {
                    if (fixture.CountRequests("/page") <= beforeEdge) throw new InvalidOperationException("External Edge never fetched the test page");
                    SmokeTest.Report("menu UI: new Edge InPrivate window requested the actual page from the HTTP server");
                }
                else if (fixture.CountRequests("/page") != beforeEdge)
                    throw new InvalidOperationException("Restricted handoff sent the page without the user opening Edge");
            }
            Click(ZoomMenuPlus);
            await WaitAsync(() => ZoomPanel.Visibility == Visibility.Visible && !Equals(ZoomPanelReset.Content, "100%"), "web plus changed the engine zoom and displayed the measured value");
            Click(ZoomPanelMinus);
            await WaitAsync(() => Equals(ZoomPanelReset.Content, "100%"), "web minus restored engine zoom");
            Click(ZoomPanelMinus);
            await WaitAsync(() => !Equals(ZoomPanelReset.Content, "100%"), "web minus reduced engine zoom");
            Click(ZoomPanelReset);
            await WaitAsync(() => Equals(ZoomPanelReset.Content, "100%"), "web reset restored engine zoom");
            HideZoomPanel();

            await _hookedCore!.ExecuteScriptAsync("document.body.append(' probe-menu-match probe-menu-match ')");
            await MenuAsync();
            Click(FindMenuButton);
            await WaitAsync(() => FindBar.Visibility == Visibility.Visible, "find menu opened the search input");
            FindTextBox.Text = "probe-menu-match";
            await WaitAsync(() => _nativeFind?.MatchCount == 2, "find located both occurrences in the webpage");
            CloseFindBar();
            await MenuAsync();
            bool initialMute = _hookedCore.IsMuted;
            Click(AudioMuteBtn);
            await WaitAsync(() => _hookedCore.IsMuted != initialMute, "mute changed the active engine state");
            Click(AudioMuteBtn);
            await WaitAsync(() => _hookedCore.IsMuted == initialMute, "unmute restored the active engine state");
            int requests = fixture.CountRequests("/page");
            Click(HardReloadMenuButton);
            await WaitAsync(() => _documentReady && fixture.CountRequests("/page") > requests, "bypass-cache menu reloaded the document");
            await MenuAsync();
            Click(SessionHistoryMenuButton);
            await WaitAsync(() => PrivacyFlyout.IsOpen && PrivacySections.SelectedItem == HistorySection, "history menu opened its list");
            PrivacyFlyout.Hide();
            await WaitAsync(() => !PrivacyFlyout.IsOpen, "history closed");
        }
        else await NavigateAuditAsync(fixture.Address + "/page");

        {
            string directory = BrowserDownloadFolders.Prepare(_workspace);
            const string name = "以往下载 菜单验收.txt", text = "previous file retained in the isolated workspace";
            await File.WriteAllTextAsync(Path.Combine(directory, name), text);
            var download = await BeginDownloadAuditAsync(fixture.Address + "/download-slow?menu-actions");
            await WaitAsync(() => download.State == Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Completed, "the actual WebView download completed", 12000);
            if (Path.GetDirectoryName(download.ResultFilePath) != directory || new FileInfo(download.ResultFilePath).Length != 4194304)
                throw new InvalidOperationException("Downloaded bytes were not saved to the selected location");
            BrowserDownloads.ClearFinished();
            T? FindInTree<T>(DependencyObject? root) where T : DependencyObject
            {
                if (root is T hit) return hit;
                if (root == null) return null;
                for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                    if (FindInTree<T>(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
                return null;
            }
            DownloadFolderDialog? FolderDialog() => Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot)
                .Select(p => FindInTree<DownloadFolderDialog>(p.Child)).FirstOrDefault(d => d != null);
            async Task CheckFolderAsync(Button button, string acknowledgement, bool store)
            {
                Click(button);
                await WaitAsync(() => FolderDialog()?.FindName("Files") is ListView v && v.Items.OfType<DownloadFolderFile>().Any(f => f.Name == name),
                    "folder displays existing files from disk after the download list was cleared");
                var dialog = FolderDialog()!;
                var list = (ListView)dialog.FindName("Files");
                if (!list.Items.OfType<DownloadFolderFile>().Any(row => row.Path == download.ResultFilePath)) throw new InvalidOperationException("Missing completed download");
                var locations = (ComboBox)dialog.FindName("Locations");
                if (Environment.GetCommandLineArgs().Contains("--expect-restricted-folder"))
                {
                    if (locations.Items.Count != 2) throw new InvalidOperationException("Original download folder was lost after restricted fallback");
                    locations.SelectedIndex = 1;
                    await WaitAsync(() => list.Items.OfType<DownloadFolderFile>().Any(row => row.Name == "previous-location.txt"), "the protected original download location remains readable");
                    locations.SelectedIndex = 0;
                    await WaitAsync(() => list.Items.OfType<DownloadFolderFile>().Any(row => row.Name == name), "returned to the writable download location");
                }
                if (store)
                {
                    list.SelectedItem = list.Items.OfType<DownloadFolderFile>().Single(row => row.Name == name);
                    Click((Button)dialog.FindName("StoreFile"));
                    await WaitAsync(() => ((TextBlock)dialog.FindName("FolderStatus")).Text.Contains("原件已保留"), "copied an existing file to the encrypted vault while keeping the original");
                    var entry = VaultService.Instance.VisibleItems.Single(item => item.OriginalFileName == name);
                    using var decrypted = VaultService.Instance.OpenDecryptedStream(entry);
                    using var reader = new StreamReader(decrypted);
                    if (reader.ReadToEnd() != text || await File.ReadAllTextAsync(Path.Combine(directory, name)) != text)
                        throw new InvalidOperationException("Copied and original file content differ");
                    list.SelectedItem = null;
                }
                if (Environment.GetCommandLineArgs().Contains("--verify-explorer")) Click((Button)dialog.FindName("OpenExplorer"));
                if (Environment.GetCommandLineArgs().Contains("--expect-shell-denied"))
                {
                    await WaitAsync(() => ((TextBlock)dialog.FindName("FolderStatus")).Text.Contains("Windows 拒绝"),
                        "Windows denial is shown and the in-app folder stays usable for " + button.Name);
                    if (((TextBox)dialog.FindName("FolderAddress")).Text != directory || list.Items.Count < 2)
                        throw new InvalidOperationException("Restricted caller lost access to files or the selectable folder path");
                }
                else if (Environment.GetCommandLineArgs().Contains("--verify-explorer"))
                    await WaitAsync(() => File.Exists(acknowledgement), "Explorer showed the exact folder for " + button.Name, 14000);
                var close = FindFixtureButton(dialog, "CloseButton") ?? throw new InvalidOperationException("The download folder has no enabled Close button");
                Click(close);
                await WaitAsync(() => FolderDialog() == null, "folder dialog closed");
            }
            await MenuAsync();
            var ack = Path.Combine(AppPaths.DataRoot, "explorer-folder-confirmed");
            await CheckFolderAsync(FolderMenuButton, ack, true);
            if (!HasBrowserControl || !CanAccessDownloads) throw new InvalidOperationException("Lost synthetic session");
            Click(DownloadsBtn);
            var flyout = EnsureDownloadsFlyout();
            await WaitAsync(() => flyout.IsOpen && flyout.Content is DownloadListControl list && list.IsLoaded, "downloads list opened");
            var list = (DownloadListControl)flyout.Content;
            var openFolder = (Button)list.FindName("CurrentFolderButton");
            await WaitAsync(() => openFolder.IsLoaded && openFolder.IsEnabled && openFolder.ActualWidth > 0,
                "download list folder command is ready");
            await CheckFolderAsync(openFolder, ack + "-again", false);
            flyout.Hide();
        }
    }
}
