using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace StealthWorkspace.Services;

// Owned by one open flyout. Filtering preserves bookmark order and identity.
internal sealed class BookmarkFilter
{
    // Keep one sequential view per large immutable snapshot. It references the
    // existing rows, and expires with that snapshot after a collection edit.
    private static readonly ConditionalWeakTable<ImmutableList<BookmarkItem>, BookmarkItem[]> SearchRows = new();
    internal static IReadOnlyList<BookmarkItem> FullScan(ImmutableList<BookmarkItem> source) =>
        source.Count >= 4096 ? SearchRows.GetValue(source, static items => items.ToArray()) : source;
    internal static IEnumerable<BookmarkItem> AvailableRows(IEnumerable<BookmarkItem> source) =>
        source is ImmutableList<BookmarkItem> { Count: >= 4096 } snapshot && SearchRows.TryGetValue(snapshot, out var rows) ? rows : source;

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
            // A one-off search need not allocate an array of every bookmark.
            // Materialize it only after an edit requires another full traversal.
            IEnumerable<BookmarkItem> candidates = narrow ? _matches : sameSource && _query.Length > 0
                ? FullScan(bookmarks) : AvailableRows(bookmarks);
            _matches = candidates.Where(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Url.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        _source = bookmarks;
        _query = query;
        return _matches;
    }
}
