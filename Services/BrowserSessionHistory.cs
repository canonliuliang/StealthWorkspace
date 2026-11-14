using System.Collections.ObjectModel;
using StealthWorkspace.Models;

namespace StealthWorkspace.Services;

internal static class BrowserSessionHistory
{
    internal static ObservableCollection<HistorySessionItem> Items { get; } = new();

    internal static void Record(string title, string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        // History lives only in memory for this unlocked browser session. Keep a
        // bounded MRU list; URL paths remain case-sensitive.
        string url = uri.AbsoluteUri;
        var previous = Items.FirstOrDefault(item => item.Url == url);
        if (previous != null) Items.Remove(previous);
        Items.Insert(0, new HistorySessionItem { Url = url, Title = string.IsNullOrWhiteSpace(title) ? uri.Host : title, VisitedAt = DateTime.Now });
        while (Items.Count > 200) Items.RemoveAt(Items.Count - 1);
    }

    internal static void UpdateTitle(string title, string address)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        for (int i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            if (item.Url != address || item.Title == title) continue;
            // Update the row without turning a background title change into a
            // new visit or resurrecting history the user explicitly cleared.
            Items[i] = new HistorySessionItem { Url = item.Url, Title = title, VisitedAt = item.VisitedAt };
            return;
        }
    }
}
