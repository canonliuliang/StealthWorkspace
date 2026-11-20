using System.Collections.Immutable;
using StealthWorkspace.Models;

namespace StealthWorkspace.Services;

internal static class BrowserAddressSuggestions
{
    // Reuse a completely scanned, small matching set as a query narrows.
    // History remains live across tabs and is checked on every keystroke.
    internal sealed class EditSession
    {
        private ImmutableList<BookmarkItem>? _source;
        private ImmutableList<BookmarkItem>? _deepScan;
        private string _query = "";
        private BookmarkItem[] _previous = new BookmarkItem[8];
        private BookmarkItem[] _current = new BookmarkItem[8];
        private int _previousCount, _currentCount;

        internal IEnumerable<BookmarkItem> Candidates(IEnumerable<BookmarkItem> bookmarks, string query)
        {
            _currentCount = 0;
            if (ReferenceEquals(_source, bookmarks) && query.StartsWith(_query, StringComparison.Ordinal))
                return new ArraySegment<BookmarkItem>(_previous, 0, _previousCount);
            return ReferenceEquals(_deepScan, bookmarks) && _deepScan != null ? BookmarkFilter.FullScan(_deepScan) : BookmarkFilter.AvailableRows(bookmarks);
        }

        internal void Match(BookmarkItem item)
        {
            if (_currentCount < _current.Length) _current[_currentCount] = item;
            _currentCount++;
        }

        internal void Finish(IEnumerable<BookmarkItem> bookmarks, string query, int inspected, bool complete)
        {
            if (bookmarks is not ImmutableList<BookmarkItem> snapshot) return;
            if (inspected >= 512) _deepScan = snapshot;
            if (!complete || query.Length == 0 || char.IsHighSurrogate(query[^1]) || _currentCount > _current.Length) return;
            (_previous, _current) = (_current, _previous);
            _source = snapshot;
            _query = query;
            _previousCount = _currentCount;
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
        int inspected = 0;
        foreach (var bookmark in edit?.Candidates(bookmarks, query) ?? BookmarkFilter.AvailableRows(bookmarks))
        {
            inspected++;
            if (!Matches(bookmark.Title, bookmark.Url)) continue;
            edit?.Match(bookmark);
            Add(bookmark.Title, bookmark.Url, true);
            if (result.Count == 8)
            {
                edit?.Finish(bookmarks, query, inspected, complete: false);
                return result;
            }
        }
        edit?.Finish(bookmarks, query, inspected, complete: true);
        foreach (var item in history)
        {
            if (Matches(item.Title, item.Url)) Add(item.Title, item.Url, false);
            if (result.Count == 8) break;
        }
        return result;
    }
}
