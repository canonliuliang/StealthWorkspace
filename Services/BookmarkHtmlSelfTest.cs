using System.Net;
using System.Net.Sockets;

namespace StealthWorkspace.Services;

internal static class BookmarkHtmlSelfTest
{
    private sealed class ChangeAfterLastRow(BookmarkItem row, Action change) : IReadOnlyList<BookmarkItem>
    {
        public int Count => 1;
        public BookmarkItem this[int index] => index == 0 ? row : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<BookmarkItem> GetEnumerator()
        {
            yield return row;
            change();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

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
        string vaultRoot = Path.Combine(root, "bookmark-export-vault");
        var vault = new VaultService(vaultRoot);
        await vault.InitializeVaultAsync("Export#123", "DecoyExport#123");
        int written = await vault.ExportBookmarksAsync(output, first.Items, default);
        var second = await BookmarkHtml.ReadAsync(output, default);
        string data = await File.ReadAllTextAsync(output);
        report(written == 2 && !data.Contains("private") && !data.Contains("<note>") && data.Contains("&amp;")
            && second.Items.Select(b => (b.Title, b.Url)).SequenceEqual(first.Items.Take(2).Select(b => (b.Title, b.Url))),
            "Bookmark HTML export escapes untrusted titles, omits credentials and round-trips ordered web addresses", "");
        report(first.Items[1].CreatedAt == DateTime.MinValue && second.Items[1].CreatedAt == DateTime.MinValue &&
            data.Split("ADD_DATE=").Length == 2 && second.Items[0].CreatedAt == first.Items[0].CreatedAt,
            "Import and re-export preserve unknown bookmark dates and the supplied UTC timestamp", "");
        try { await vault.ExportBookmarksAsync(output, [], new CancellationToken(true)); }
        catch (OperationCanceledException) { }
        report(await File.ReadAllTextAsync(output) == data, "Cancelled bookmark export preserves the previous file", "");
        try { await vault.ExportBookmarksAsync(output, [new BookmarkItem { Url = "file:///private" }], default); }
        catch (InvalidDataException) { }
        report(await File.ReadAllTextAsync(output) == data, "An export with no portable addresses preserves the previous file", "");
        first.Items[0].CreatedAt = DateTime.MinValue;
        await vault.ExportBookmarksAsync(output, first.Items, default);
        var restored = await BookmarkHtml.ReadAsync(output, default);
        report(restored.Items.Count == 2 && restored.Items[0].Title == first.Items[0].Title,
            "Bookmark HTML exports legacy entries with unknown creation dates", "");
        string previous = await File.ReadAllTextAsync(output);
        bool isolated = true;
        foreach (string? unlock in new string?[] { null, "Export#123", "DecoyExport#123" })
        {
            var switched = new ChangeAfterLastRow(new BookmarkItem { Title = "Do not commit", Url = "https://fixture.invalid/new" }, () =>
            {
                vault.Lock();
                if (unlock != null && !vault.ApplyUnlock(vault.VerifyPassword(unlock))) throw new InvalidOperationException("Could not switch test session");
            });
            try { await vault.ExportBookmarksAsync(output, switched, default); isolated = false; }
            catch (OperationCanceledException) { }
            isolated &= await File.ReadAllTextAsync(output) == previous && !Directory.EnumerateFiles(root, ".stealth-bookmarks-*.tmp").Any();
            vault.Lock();
            if (!vault.ApplyUnlock(vault.VerifyPassword("Export#123"))) throw new InvalidOperationException("Could not restore test session");
        }
        report(isolated, "Lock, same-space re-unlock and decoy switch before bookmark export commit preserve the old file", "");
        string internalTarget = Path.Combine(vaultRoot, "private.html");
        await File.WriteAllTextAsync(internalTarget, "preserve original");
        bool guarded = false;
        try { await vault.ExportBookmarksAsync(internalTarget, first.Items, default); }
        catch (InvalidDataException) { guarded = true; }
        vault.Lock();
        bool locked = false;
        try { await vault.ExportBookmarksAsync(output, first.Items, default); }
        catch (OperationCanceledException) { locked = true; }
        report(locked && guarded && await File.ReadAllTextAsync(internalTarget) == "preserve original" &&
            await File.ReadAllTextAsync(output) == previous && !Directory.EnumerateFiles(vaultRoot, ".stealth-bookmarks-*.tmp").Any(),
            "Already locked bookmark exports and destinations inside the vault leave existing files intact", "");
        using (var large = File.Create(html)) large.SetLength(BookmarkHtml.MaxImportBytes + 1L);
        try
        {
            await BookmarkHtml.ReadAsync(html, default);
            report(false, "Bookmark HTML enforces input size before parsing", "");
        }
        catch (InvalidDataException) { report(true, "Bookmark HTML enforces input size before parsing", ""); }
    }
}
