using System.Globalization;
using System.Net;
using System.Text;
using AngleSharp.Html.Parser;

namespace StealthWorkspace.Services;

internal static class BookmarkHtml
{
    internal const int MaxImportBytes = 32 * 1024 * 1024;
    internal sealed record ImportResult(List<BookmarkItem> Items, int Skipped);

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
        int skipped = 0;
        foreach (var anchor in document.GetElementsByTagName("a"))
        {
            token.ThrowIfCancellationRequested();
            if (!IsPortableAddress(anchor.GetAttribute("href"), out var address))
            {
                skipped++;
                continue;
            }
            var item = new BookmarkItem { Url = address!.AbsoluteUri, Title = anchor.TextContent.Trim() };
            if (string.IsNullOrEmpty(item.Title)) item.Title = address.Host;
            if (long.TryParse(anchor.GetAttribute("add_date"), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
                && seconds is >= 0 and <= 253402214399)
                item.CreatedAt = DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
            items.Add(item);
        }
        return new(items, skipped);
    }

    internal static int Write(string path, IReadOnlyList<BookmarkItem> items, CancellationToken token)
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
                foreach (var item in items)
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsPortableAddress(item.Url, out var address)) continue;
                    string url = WebUtility.HtmlEncode(address!.AbsoluteUri);
                    string title = WebUtility.HtmlEncode(item.Title);
                    string added = "";
                    // ADD_DATE is optional. Older lists may contain an unknown date
                    // that cannot be converted to a local DateTimeOffset.
                    if (item.CreatedAt.Year is >= 1970 and < 9999)
                    {
                        long timestamp = new DateTimeOffset(item.CreatedAt).ToUnixTimeSeconds();
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
            if (written == 0) throw new InvalidDataException("没有可以导出的 HTTP 或 HTTPS 收藏。");
            File.Move(temp, destination, overwrite: true);
            return written;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
