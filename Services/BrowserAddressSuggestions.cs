using System.Collections.Immutable;
using StealthWorkspace.Models;

namespace StealthWorkspace.Services;

internal static class BrowserAddressSuggestions
{
    // An editing session can reuse a proven miss as the user keeps typing. Only
    // the immutable bookmark snapshot participates: history changes across tabs.
    internal sealed class EditSession
    {
        private ImmutableList<BookmarkItem>? _source;
        private string _miss = "";

        internal bool CanSkip(IEnumerable<BookmarkItem> bookmarks, string query) =>
            ReferenceEquals(_source, bookmarks) && query.StartsWith(_miss, StringComparison.Ordinal);

        internal void RememberMiss(IEnumerable<BookmarkItem> bookmarks, string query)
        {
            if (bookmarks is ImmutableList<BookmarkItem> snapshot && query.Length > 0 && !char.IsHighSurrogate(query[^1]))
            {
                _source = snapshot;
                _miss = query;
            }
        }
    }

    internal static IReadOnlyList<BrowserAddressSuggestion> Get(string text, IEnumerable<BookmarkItem> bookmarks,
        IEnumerable<HistorySessionItem> history, Func<string, string> search, EditSession? edit = null)
    {
        string query = text.Trim();
        var result = new List<BrowserAddressSuggestion>(8);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (query.Length > 0 && BrowserAddress.Resolve(query, search, out bool isSearch) is { } target)
        {
            result.Add(new BrowserAddressSuggestion { Title = isSearch ? $"搜索“{query}”" : "访问 " + target.Host,
                Detail = isSearch ? "使用当前搜索引擎" : target.AbsoluteUri, Input = query, Target = target.AbsoluteUri,
                Glyph = isSearch ? "\uE721" : "\uE774" });
            seen.Add(target.AbsoluteUri);
        }

        bool Matches(string title, string url) => query.Length == 0 ||
            title.Contains(query, StringComparison.OrdinalIgnoreCase) || url.Contains(query, StringComparison.OrdinalIgnoreCase);

        void Add(string title, string url, bool bookmark)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
            if (!seen.Add(uri.AbsoluteUri)) return;
            result.Add(new BrowserAddressSuggestion { Title = string.IsNullOrWhiteSpace(title) ? uri.Host : title,
                Detail = (bookmark ? "收藏 · " : "本次会话 · ") + uri.AbsoluteUri, Input = uri.AbsoluteUri,
                Target = uri.AbsoluteUri, Glyph = bookmark ? "\uE734" : "\uE81C" });
        }
        if (edit?.CanSkip(bookmarks, query) != true)
        {
            bool matched = false;
            foreach (var bookmark in bookmarks)
            {
                if (!Matches(bookmark.Title, bookmark.Url)) continue;
                matched = true;
                Add(bookmark.Title, bookmark.Url, true);
                if (result.Count == 8) return result;
            }
            if (!matched) edit?.RememberMiss(bookmarks, query);
        }
        foreach (var item in history)
        {
            if (Matches(item.Title, item.Url)) Add(item.Title, item.Url, false);
            if (result.Count == 8) break;
        }
        return result;
    }
}
