using System.Collections.Immutable;

namespace StealthWorkspace.Services;

// Owned by one open flyout. Filtering preserves bookmark order and identity.
internal sealed class BookmarkFilter
{
    private ImmutableList<BookmarkItem>? _source;
    private string _query = "";
    private IReadOnlyList<BookmarkItem> _matches = Array.Empty<BookmarkItem>();

    internal IReadOnlyList<BookmarkItem> Filter(ImmutableList<BookmarkItem> bookmarks, string text)
    {
        string query = text.Trim();
        bool sameSource = ReferenceEquals(bookmarks, _source);
        if (sameSource && string.Equals(query, _query, StringComparison.OrdinalIgnoreCase)) return _matches;

        if (query.Length == 0)
        {
            _matches = bookmarks;
        }
        else
        {
            // A new low surrogate can change the case folding of the preceding
            // high surrogate. Recheck the original list when crossing that boundary.
            bool narrow = sameSource && _query.Length > 0 && !char.IsHighSurrogate(_query[^1]) &&
                query.StartsWith(_query, StringComparison.Ordinal);
            IReadOnlyList<BookmarkItem> candidates = narrow ? _matches : bookmarks;
            _matches = candidates.Where(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Url.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        _source = bookmarks;
        _query = query;
        return _matches;
    }
}
