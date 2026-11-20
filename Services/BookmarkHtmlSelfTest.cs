using System.Net;
using System.Net.Sockets;

namespace StealthWorkspace.Services;

internal static class BookmarkHtmlSelfTest
{
    internal static async Task RunAsync(string root, Action<bool, string, string> report)
    {
        string html = Path.Combine(root, "bookmarks.html");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string local = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        await File.WriteAllTextAsync(html, $$"""
            <!DOCTYPE NETSCAPE-Bookmark-file-1>
            <META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
            <BASE HREF="{{local}}/"><SCRIPT SRC="{{local}}/test.js"></SCRIPT><IMG SRC="{{local}}/icon">
            <DL><p><DT><H3>Research</H3><DL><p>
            <DT><A
               HREF="https://example.org/Case?x=1&amp;y=2#demo" ADD_DATE="1700000000">中文 &lt;note&gt; &amp; α</A>
            <DT><A HREF='relative'>Relative</A><DT><A HREF='file:///C:/example'>Local</A>
            <DT><A HREF='javascript:void(0)'>Script</A><DT><A HREF='https://user:pass@example.org/'>Private</A>
            <DT><A HREF='https://example.org/Other'></A></DL></DL>
            """);
        var first = await BookmarkHtml.ReadAsync(html, default);
        report(first.Items.Count == 2 && first.Skipped == 4 && first.Items[0].Title == "中文 <note> & α"
            && first.Items[0].Url == "https://example.org/Case?x=1&y=2#demo" && first.Items[1].Title == "example.org"
            && new DateTimeOffset(first.Items[0].CreatedAt).ToUnixTimeSeconds() == 1700000000,
            "Bookmark HTML reads nested, multiline, Unicode and escaped bookmarks without importing credentials or non-Web URLs", "");
        report(!listener.Pending(), "Bookmark HTML loader makes no connection to the embedded loopback script or image", "");
        first.Items.Add(new BookmarkItem { Url = "https://user:private@example.org", Title = "Excluded" });
        string output = Path.Combine(root, "bookmarks-export.html");
        int written = BookmarkHtml.Write(output, first.Items, default);
        var second = await BookmarkHtml.ReadAsync(output, default);
        string data = await File.ReadAllTextAsync(output);
        report(written == 2 && !data.Contains("private") && !data.Contains("<note>") && data.Contains("&amp;")
            && second.Items.Select(b => (b.Title, b.Url)).SequenceEqual(first.Items.Take(2).Select(b => (b.Title, b.Url))),
            "Bookmark HTML export escapes untrusted titles, omits credentials and round-trips ordered web addresses", "");
        try { BookmarkHtml.Write(output, [], new CancellationToken(true)); }
        catch (OperationCanceledException) { }
        report(await File.ReadAllTextAsync(output) == data, "Cancelled bookmark export preserves the previous file", "");
        try { BookmarkHtml.Write(output, [new BookmarkItem { Url = "file:///private" }], default); }
        catch (InvalidDataException) { }
        report(await File.ReadAllTextAsync(output) == data, "An export with no portable addresses preserves the previous file", "");
        first.Items[0].CreatedAt = DateTime.MinValue;
        BookmarkHtml.Write(output, first.Items, default);
        var restored = await BookmarkHtml.ReadAsync(output, default);
        report(restored.Items.Count == 2 && restored.Items[0].Title == first.Items[0].Title,
            "Bookmark HTML exports legacy entries with unknown creation dates", "");
        using (var large = File.Create(html)) large.SetLength(BookmarkHtml.MaxImportBytes + 1L);
        try
        {
            await BookmarkHtml.ReadAsync(html, default);
            report(false, "Bookmark HTML enforces input size before parsing", "");
        }
        catch (InvalidDataException) { report(true, "Bookmark HTML enforces input size before parsing", ""); }
    }
}
