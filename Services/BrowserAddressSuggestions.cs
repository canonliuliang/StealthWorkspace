using StealthWorkspace.Models;

namespace StealthWorkspace.Services;

internal static class BrowserAddressSuggestions
{
    internal static IReadOnlyList<BrowserAddressSuggestion> Get(string text, IEnumerable<BookmarkItem> bookmarks,
        IEnumerable<HistorySessionItem> history, Func<string, string> search)
    {
        string query = text.Trim();
        var result = new List<BrowserAddressSuggestion>(8);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (query.Length > 0 && BrowserAddress.Resolve(query, search) is { } target)
        {
            bool isSearch = target.AbsoluteUri == search(query);
            result.Add(new BrowserAddressSuggestion { Title = isSearch ? $"搜索“{query}”" : "访问 " + target.Host,
                Detail = isSearch ? "使用当前搜索引擎" : target.AbsoluteUri, Input = query, Target = target.AbsoluteUri,
                Glyph = isSearch ? "\uE721" : "\uE774" });
            seen.Add(target.AbsoluteUri);
        }

        void Add(string title, string url, bool bookmark)
        {
            if (result.Count >= 8) return;
            // Most entries do not match a typed query. Keep that hot path to two
            // string scans; constructing thousands of Uri objects on each key
            // press would allocate and parse on the XAML input thread.
            if (query.Length > 0 && !title.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !url.Contains(query, StringComparison.OrdinalIgnoreCase)) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
            if (!seen.Add(uri.AbsoluteUri)) return;
            result.Add(new BrowserAddressSuggestion { Title = string.IsNullOrWhiteSpace(title) ? uri.Host : title,
                Detail = (bookmark ? "收藏 · " : "本次会话 · ") + uri.AbsoluteUri, Input = uri.AbsoluteUri,
                Target = uri.AbsoluteUri, Glyph = bookmark ? "\uE734" : "\uE81C" });
        }
        foreach (var bookmark in bookmarks) { if (result.Count >= 8) break; Add(bookmark.Title, bookmark.Url, true); }
        foreach (var item in history) { if (result.Count >= 8) break; Add(item.Title, item.Url, false); }
        return result;
    }
}
