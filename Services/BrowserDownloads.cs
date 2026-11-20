using System.Collections.ObjectModel;
using Microsoft.Web.WebView2.Core;
using StealthWorkspace.Models;

namespace StealthWorkspace.Services;

/// <summary>UI-thread session history shared by every tab. No download URLs are persisted.</summary>
internal static class BrowserDownloads
{
    internal static ObservableCollection<BrowserDownloadItem> Items { get; } = new();
    internal static int PendingCount { get; private set; }
    internal static event Action? PendingCountChanged;

    internal static void RefreshPendingCount()
    {
        int count = Items.Count(i => i.IsPending);
        if (PendingCount == count) return;
        PendingCount = count;
        PendingCountChanged?.Invoke();
    }

    internal static BrowserDownloadItem Add(CoreWebView2DownloadOperation operation, string path, Action wakeOwner)
    {
        var item = new BrowserDownloadItem(operation, path, wakeOwner);
        Items.Insert(0, item);
        RefreshPendingCount();
        while (Items.Count > 200)
        {
            var oldest = Items.LastOrDefault(i => !i.IsPending);
            if (oldest == null) break;
            Items.Remove(oldest);
        }
        return item;
    }

    internal static void ClearFinished()
    {
        foreach (var item in Items.Where(i => !i.IsPending).ToArray()) Items.Remove(item);
    }

    internal static void EndSession()
    {
        foreach (var item in Items.ToArray())
        {
            try { if (item.IsPending) item.Cancel(); }
            catch (Exception ex) { AppLog.Warn("Cancel session download: " + ex.GetType().Name); }
            item.Detach("会话已结束");
        }
        Items.Clear();
    }
}
