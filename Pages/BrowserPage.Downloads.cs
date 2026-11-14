using Microsoft.UI.Xaml.Controls;
using StealthWorkspace.Controls;

namespace StealthWorkspace.Pages;

public sealed partial class BrowserPage
{
    private readonly Dictionary<BrowserTabControl, Action> _downloadOwners = new();

    private void RetainDownloadOwner(BrowserTabControl tab)
    {
        Action changed = () => DispatcherQueue.TryEnqueue(async () =>
        {
            if (tab.HasPendingDownloads || !_downloadOwners.Remove(tab, out var handler)) return;
            tab.DownloadsActivityChanged -= handler;
            DownloadHosts.Children.Remove(tab);
            await tab.DisposeTabAsync();
        });
        _downloadOwners.Add(tab, changed);
        tab.DownloadsActivityChanged += changed;
        tab.RetainOnlyForDownloads();
        DownloadHosts.Children.Add(tab);
        changed(); // Completion can arrive between checking and retiring the tab.
    }

    private void DetachDownloadOwners()
    {
        foreach (var (tab, changed) in _downloadOwners) tab.DownloadsActivityChanged -= changed;
        _downloadOwners.Clear();
        DownloadHosts.Children.Clear();
    }
}
