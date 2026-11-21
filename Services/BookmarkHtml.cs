using System.Globalization;
using System.Net;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace StealthWorkspace.Services;

internal static class BookmarkHtml
{
    internal const int MaxImportBytes = 32 * 1024 * 1024;
    internal sealed record ImportResult(List<BookmarkItem> Items, List<BookmarkFolder> Folders, int Skipped);

    private static bool IsPortableAddress(string? value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https"
        && uri.Host.Length > 0 && uri.UserInfo.Length == 0;

    internal static async Task<ImportResult> ReadAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxImportBytes) throw new InvalidDataException("收藏文件超过 32 MiB。请在本地将副本拆成较小的 HTML 文件，分次导入。");
        // A standalone HTML parser has no browsing context with a resource loader or script engine.
        using var document = await new HtmlParser().ParseDocumentAsync(file, token).ConfigureAwait(false);
        var items = new List<BookmarkItem>();
        var folders = new List<BookmarkFolder>();
        var paths = new Dictionary<IElement, string>();
        var toolbar = document.QuerySelector("h3[personal_toolbar_folder='true' i]");
        var other = new BookmarkFolder(BookmarkFolder.Other, "", "其他收藏");
        var order = 0;
        int skipped = 0;
        foreach (var element in document.QuerySelectorAll("dl,a"))
        {
            token.ThrowIfCancellationRequested();
            var parentList = element.ParentElement?.Closest("dl");
            string parent = parentList != null && paths.TryGetValue(parentList, out var owner) ? owner : toolbar != null ? other.Id : "";
            if (element.LocalName == "dl")
            {
                var previous = element.PreviousElementSibling;
                if (previous?.LocalName == "dt") previous = previous.QuerySelector("h3");
                if (previous?.LocalName == "h3")
                {
                    if (ReferenceEquals(previous, toolbar)) parent = "";
                    else
                    {
                        IncludeOther(parent);
                        string title = previous.TextContent.Trim();
                        if (title.Length == 0) title = "未命名文件夹";
                        if (title.Length > 200) title = title[..(char.IsHighSurrogate(title[199]) ? 199 : 200)];
                        var folder = new BookmarkFolder(Guid.NewGuid().ToString("N"), parent, title, order++);
                        folders.Add(folder);
                        parent = folder.Id;
                    }
                }
                paths[element] = parent;
                continue;
            }
            var anchor = element;
            if (!IsPortableAddress(anchor.GetAttribute("href"), out var address))
            {
                skipped++;
                continue;
            }
            IncludeOther(parent);
            var item = new BookmarkItem { Url = address!.AbsoluteUri, Title = anchor.TextContent.Trim(), CreatedAt = DateTime.MinValue, FolderId = parent, Position = order++ };
            if (string.IsNullOrEmpty(item.Title)) item.Title = address.Host;
            if (long.TryParse(anchor.GetAttribute("add_date"), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
                && seconds is >= 0 and <= 253402300799)
                item.CreatedAt = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            items.Add(item);
        }
        return new(items, folders, skipped);

        void IncludeOther(string id)
        {
            if (id == other.Id && (folders.Count == 0 || folders[0].Id != other.Id)) folders.Insert(0, other);
        }
    }

    internal static int Write(string path, IReadOnlyList<BookmarkItem> items, CancellationToken token,
        Action<string, string> commit, IReadOnlyList<BookmarkFolder>? folders = null)
    {
        token.ThrowIfCancellationRequested();
        string destination = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(destination) ?? throw new IOException("无法定位导出目录。");
        string temp = Path.Combine(parent, ".stealth-bookmarks-" + Guid.NewGuid().ToString("N") + ".tmp");
        int written = 0;
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
            {
                writer.WriteLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
                writer.WriteLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
                writer.WriteLine("<TITLE>Stealth Bookmarks</TITLE><H1>Stealth Bookmarks</H1>");
                writer.WriteLine("<DL><p>");
                if (folders == null)
                {
                    foreach (var item in items) WriteLink(item);
                }
                else
                {
                    var entries = folders.Cast<object>().Concat(items).ToLookup(row =>
                        row is BookmarkFolder f ? f.ParentId : ((BookmarkItem)row).FolderId);
                    var seen = new HashSet<string>();
                    void WriteChildren(string root)
                    {
                        var stack = new Stack<object?>();
                        void PushChildren(string id)
                        {
                            foreach (var row in entries[id].OrderBy(row =>
                                (row is BookmarkFolder f ? f.Position : ((BookmarkItem)row).Position) ?? int.MaxValue).Reverse())
                                if (row is not BookmarkFolder { Id: BookmarkFolder.Other }) stack.Push(row);
                        }
                        PushChildren(root);
                        while (stack.TryPop(out var row))
                        {
                            token.ThrowIfCancellationRequested();
                            if (row == null) writer.WriteLine("</DL><p>");
                            else if (row is BookmarkItem link) WriteLink(link);
                            else if (row is BookmarkFolder f && seen.Add(f.Id))
                            {
                                writer.WriteLine($"<DT><H3>{WebUtility.HtmlEncode(f.Name)}</H3>");
                                writer.WriteLine("<DL><p>");
                                stack.Push(null);
                                PushChildren(f.Id);
                            }
                        }
                    }
                    writer.WriteLine("<DT><H3 PERSONAL_TOOLBAR_FOLDER=\"true\">收藏栏</H3>");
                    writer.WriteLine("<DL><p>");
                    WriteChildren("");
                    writer.WriteLine("</DL><p>");
                    WriteChildren(BookmarkFolder.Other);
                }
                void WriteLink(BookmarkItem item)
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsPortableAddress(item.Url, out var address)) return;
                    string url = WebUtility.HtmlEncode(address!.AbsoluteUri);
                    string title = WebUtility.HtmlEncode(item.Title);
                    string added = "";
                    // ADD_DATE is optional. Older lists may contain an unknown date
                    // that cannot be converted to a local DateTimeOffset.
                    if (item.CreatedAt != DateTime.MinValue)
                    {
                        long timestamp = new DateTimeOffset(item.CreatedAt.ToUniversalTime()).ToUnixTimeSeconds();
                        if (timestamp >= 0) added = $" ADD_DATE=\"{timestamp.ToString(CultureInfo.InvariantCulture)}\"";
                    }
                    writer.WriteLine($"    <DT><A HREF=\"{url}\"{added}>{title}</A>");
                    written++;
                }
                writer.WriteLine("</DL><p>");
                writer.Flush();
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            if (written == 0 && (folders == null || folders.Count == 0)) throw new InvalidDataException("没有可以导出的 HTTP 或 HTTPS 收藏。");
            commit(temp, destination);
            return written;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
