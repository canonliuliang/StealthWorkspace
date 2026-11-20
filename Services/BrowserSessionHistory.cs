using System.Collections.ObjectModel;
using StealthWorkspace.Models;

namespace StealthWorkspace.Services;

internal static class BrowserSessionHistory
{
    internal static ObservableCollection<HistorySessionItem> Items { get; } = new();
    private static readonly List<ClosedBrowserTab> Closed = new();
    internal static IReadOnlyList<ClosedBrowserTab> RecentlyClosed => Closed;

    internal static void RememberClosed(string title, string address, int position)
    {
        if (position < 0 || !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https")) return;
        Closed.Insert(0, new ClosedBrowserTab(string.IsNullOrWhiteSpace(title) ? uri.Host : title, uri.AbsoluteUri, position));
        if (Closed.Count > 20) Closed.RemoveAt(Closed.Count - 1);
    }

    internal static void RemoveClosed(ClosedBrowserTab item) => Closed.Remove(item);

    internal static void Forget(HistorySessionItem item)
    {
        Closed.RemoveAll(tab => tab.Url == item.Url);
        Items.Remove(item);
    }

    internal static void Clear()
    {
        Closed.Clear();
        Items.Clear();
    }

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

// Keep only the information required to reopen an address in this unlocked
// session. Never retain a controller, document, POST body, or form value.
internal sealed class ClosedBrowserTab(string title, string url, int position)
{
    internal string Title { get; } = title;
    internal string Url { get; } = url;
    internal int Position { get; } = position;
}
