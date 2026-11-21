using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace StealthWorkspace.Services;

internal sealed record BookmarkLibrary(ImmutableList<BookmarkItem> Items, ImmutableList<BookmarkFolder> Folders)
{
    private static readonly ConditionalWeakTable<ImmutableList<BookmarkFolder>, Dictionary<string, BookmarkFolder>> FolderIndexes = new();
    // Use both immutable snapshots as weak keys. Tabs in the same workspace
    // share the arrays; edits to folders alone invalidate the matching index.
    private static readonly ConditionalWeakTable<ImmutableList<BookmarkItem>,
        ConditionalWeakTable<ImmutableList<BookmarkFolder>, Dictionary<string, object[]>>> ChildIndexes = new();
    private Dictionary<string, BookmarkFolder> ById => FolderIndexes.GetValue(Folders,
        static folders => folders.ToDictionary(f => f.Id, StringComparer.Ordinal));
    internal string PathName(string id)
    {
        var names = new Stack<string>();
        var seen = new HashSet<string>();
        while (id.Length > 0 && seen.Add(id) && ById.TryGetValue(id, out var folder))
        {
            names.Push(folder.Name);
            id = folder.ParentId;
        }
        return names.Count == 0 ? "收藏栏" : string.Join(" › ", names);
    }

    internal IEnumerable<object> Children(string id)
    {
        if (Items.Count + Folders.Count < 256)
            return Folders.Where(f => f.ParentId == id).Cast<object>().Concat(Items.Where(b => b.FolderId == id)).OrderBy(Position);
        var indexes = ChildIndexes.GetValue(Items, static _ => new());
        if (!indexes.TryGetValue(Folders, out var groups)) groups = indexes.GetValue(Folders, BuildChildIndex);
        return groups.TryGetValue(id, out var rows) ? rows : Array.Empty<object>();
    }

    private Dictionary<string, object[]> BuildChildIndex(ImmutableList<BookmarkFolder> folders) =>
        folders.Cast<object>().Concat(Items)
            .GroupBy(row => row is BookmarkFolder f ? f.ParentId : ((BookmarkItem)row).FolderId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(Position).ToArray(), StringComparer.Ordinal);

    private static int Position(object row) =>
        (row is BookmarkFolder f ? f.Position : ((BookmarkItem)row).Position) ?? int.MaxValue;

    internal bool IsDescendant(string id, string ancestor)
    {
        var seen = new HashSet<string>();
        while (id.Length > 0 && seen.Add(id))
        {
            if (id == ancestor) return true;
            id = ById.TryGetValue(id, out var folder) ? folder.ParentId : "";
        }
        return false;
    }
}

public partial class SettingsService
{
    internal bool HasBookmarkFolders => _bookmarkSession is { } session && (session.Temporary ? session.Folders.Count : Data.BookmarkFolders.Count) > 0;
    internal BookmarkLibrary GetBookmarkLibrary()
    {
        lock (_saveGate)
        {
            var session = _bookmarkSession;
            return new(BookmarksFor(session), session == null ? [] : session.Temporary ? session.Folders : Data.BookmarkFolders);
        }
    }

    private bool SaveLibrary(BookmarkSession session, BookmarkLibrary library)
    {
        if (!ReferenceEquals(session, _bookmarkSession)) return false;
        if (!session.Temporary) return SaveSettings(library.Items, folderChange: library.Folders);
        session.Items = library.Items;
        session.Folders = library.Folders;
        NotifySettingsChanged();
        return true;
    }

    internal bool EditBookmarkFolder(BookmarkSession session, string? id, string parentId, string title)
    {
        title = title.Trim();
        if (title.Length is 0 or > 200) return false;
        lock (_saveGate)
        {
            if (!ReferenceEquals(session, _bookmarkSession)) return false;
            var library = GetBookmarkLibrary();
            var existing = id == null ? null : library.Folders.Find(f => f.Id == id);
            if ((id != null && existing == null) || (parentId != "" && !library.Folders.Exists(f => f.Id == parentId))
                || (id != null && library.IsDescendant(parentId, id))
                || (id == BookmarkFolder.Other && (parentId != "" || title != existing!.Name))
                || library.Folders.Exists(f => f.Id != id && f.ParentId == parentId && string.Equals(f.Name, title, StringComparison.OrdinalIgnoreCase))) return false;
            var folder = new BookmarkFolder(id ?? Guid.NewGuid().ToString("N"), parentId, title, existing?.ParentId == parentId ? existing.Position : null);
            return SaveLibrary(session, library with { Folders = existing == null ? library.Folders.Add(folder) : library.Folders.Replace(existing, folder) });
        }
    }

    internal bool EditBookmark(BookmarkSession session, BookmarkItem? existing, string title, string url, string folderId)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("https" or "http") || address.UserInfo.Length != 0) return false;
        lock (_saveGate)
        {
            if (!ReferenceEquals(session, _bookmarkSession)) return false;
            var library = GetBookmarkLibrary();
            if ((folderId != "" && !library.Folders.Exists(f => f.Id == folderId)) || (existing != null && !library.Items.Contains(existing))) return false;
            if (library.Items.Any(b => b != existing && b.FolderId == folderId && BrowserAddress.SamePage(b.Url, url))) return false;
            var item = NewBookmark(title, address, existing?.CreatedAt ?? DateTime.Now, folderId,
                existing?.FolderId == folderId ? existing.Position : null);
            return SaveLibrary(session, library with { Items = existing == null ? library.Items.Insert(0, item) : library.Items.Replace(existing, item) });
        }
    }

    internal bool RemoveBookmarkEntry(BookmarkSession session, object row)
    {
        lock (_saveGate)
        {
            if (!ReferenceEquals(session, _bookmarkSession)) return false;
            var library = GetBookmarkLibrary();
            if (row is BookmarkItem item && library.Items.Contains(item))
                return SaveLibrary(session, library with { Items = library.Items.Remove(item) });
            if (row is not BookmarkFolder folder || !library.Folders.Contains(folder)) return false;
            var ids = library.Folders.Where(f => library.IsDescendant(f.Id, folder.Id)).Select(f => f.Id).ToHashSet();
            return SaveLibrary(session, new(library.Items.RemoveAll(b => ids.Contains(b.FolderId)), library.Folders.RemoveAll(f => ids.Contains(f.Id))));
        }
    }

    internal bool ImportBookmarkLibrary(BookmarkHtml.ImportResult incoming, BookmarkSession session, CancellationToken token, out int added, out int foldersAdded)
    {
        added = foldersAdded = 0;
        lock (_saveGate)
        {
            if (!ReferenceEquals(session, PersonalBookmarkSession)) return false;
            token.ThrowIfCancellationRequested();
            var library = GetBookmarkLibrary();
            var folders = library.Folders.ToBuilder();
            var folderMap = new Dictionary<string,string> { [""] = "" };
            var existingGroups = folders.ToLookup(f => (f.ParentId, f.Name));
            var occurrence = new Dictionary<(string,string),int>();
            foreach (var folder in incoming.Folders)
            {
                token.ThrowIfCancellationRequested();
                string parent = folderMap[folder.ParentId];
                var key = (parent, folder.Name);
                int ordinal = occurrence.GetValueOrDefault(key);
                occurrence[key] = ordinal + 1;
                var existing = folder.Id == BookmarkFolder.Other ? library.Folders.Find(f => f.Id == BookmarkFolder.Other) : existingGroups[key].ElementAtOrDefault(ordinal);
                if (existing == null)
                {
                    existing = folder with { Id = folder.Id == BookmarkFolder.Other ? folder.Id : Guid.NewGuid().ToString("N"), ParentId = parent };
                    folders.Add(existing);
                }
                folderMap.Add(folder.Id, existing.Id);
            }
            var identities = library.Items.Select(b => (b.FolderId, BrowserAddress.PageKey.From(b.Url))).ToHashSet();
            var items = new List<BookmarkItem>();
            foreach (var b in incoming.Items)
            {
                token.ThrowIfCancellationRequested();
                if (!Uri.TryCreate(b.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) continue;
                string folder = folderMap[b.FolderId];
                if (identities.Add((folder, BrowserAddress.PageKey.From(uri)))) items.Add(NewBookmark(b.Title, uri, b.CreatedAt, folder, b.Position));
            }
            token.ThrowIfCancellationRequested();
            if ((folders.Count != library.Folders.Count || items.Count != 0) &&
                !SaveLibrary(session, new(library.Items.InsertRange(0, items), folders.ToImmutable()))) return false;
            added = items.Count;
            foldersAdded = folders.Count - library.Folders.Count;
            return true;
        }
    }
}
