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
        await CheckFolderWorkflowAsync(root, report);
    }

    private static async Task CheckFolderWorkflowAsync(string root, Action<bool,string,string> report)
    {
        string source = Path.Combine(root, "chrome-bookmark-folders.html");
        await File.WriteAllTextAsync(source, """
            <!DOCTYPE NETSCAPE-Bookmark-file-1>
            <META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
            <TITLE>Bookmarks</TITLE><H1>Bookmarks</H1><DL><p>
              <DT><H3 PERSONAL_TOOLBAR_FOLDER="true">Bookmarks bar</H3><DL><p>
                <DT><A HREF="https://video.example/watch?v=7">片段入口</A>
                <DT><H3>视频</H3><DL><p>
                  <DT><H3>片段</H3><DL><p>
                    <DT><A HREF="https://video.example/watch?v=7">第七集</A>
                    <DT><A HREF="https://video.example/watch?v=7">同夹重复</A>
                  </DL><p>
                </DL><p>
                <DT><H3>图片 &amp; 图册</H3><DL><p></DL><p>
              </DL><p>
              <DT><A HREF="https://news.example/">其他链接</A>
            </DL><p>
            """);
        string settingsRoot = Path.Combine(root, "chrome-bookmark-data");
        var settings = new SettingsService(settingsRoot, null);
        var session = settings.PersonalBookmarkSession!;
        settings.AddBookmark("旧收藏", "https://legacy.example/");
        var parsed = await BookmarkHtml.ReadAsync(source, default);
        bool imported = settings.ImportBookmarkLibrary(parsed, session, default, out int added, out int foldersAdded);
        var library = settings.GetBookmarkLibrary();
        var folder = library.Folders.Single(f => f.Name == "视频");
        var nested = library.Folders.Single(f => f.Name == "片段");
        var photos = library.Folders.Single(f => f.Name == "图片 & 图册");
        var other = library.Folders.Single(f => f.Id == BookmarkFolder.Other);
        report(imported && added == 3 && foldersAdded == 4 && nested.ParentId == folder.Id && folder.ParentId == "" &&
            photos.ParentId == "" && !library.Children(photos.Id).Any() && library.Children(other.Id).OfType<BookmarkItem>().Single().Title == "其他链接" &&
            library.Children("").Take(3).Select(x => x.ToString()).SequenceEqual(new[]{"片段入口", "视频", "图片 & 图册"}),
            "Chrome toolbar marker, mixed order, nested and empty folders and non-toolbar links retain their places", "");
        report(library.Items.Count(b => b.Url == "https://video.example/watch?v=7") == 2 && settings.ImportBookmarkLibrary(parsed, session, default, out added, out foldersAdded)
            && added == 0 && foldersAdded == 0 && library.Items.Single(b => b.Title == "旧收藏").FolderId == "",
            "Reimport skips duplicates within each folder while preserving the same URL in distinct folders and legacy root bookmarks", "");
        var fresh = new SettingsService(settingsRoot, null).GetBookmarkLibrary();
        report(fresh.Items.Count == library.Items.Count && fresh.Folders.SequenceEqual(library.Folders) &&
            fresh.Items.Select(b => b.FolderId).SequenceEqual(library.Items.Select(b => b.FolderId)),
            "Bookmark folders and bookmark parents survive encrypted settings reload", "");
        string html = Path.Combine(root, "chrome-round-trip-folders.html");
        BookmarkHtml.Write(html, library.Items, default, (tmp,dest) => File.Move(tmp,dest,true), library.Folders);
        var again = await BookmarkHtml.ReadAsync(html, default);
        var clone = new SettingsService(Path.Combine(root,"chrome-reimport"), null);
        clone.ImportBookmarkLibrary(again,clone.PersonalBookmarkSession!,default,out _,out _);
        var importedAgain = clone.GetBookmarkLibrary();
        report(importedAgain.Items.Select(b => (b.Title,b.Url,Path:importedAgain.PathName(b.FolderId))).OrderBy(b=>b.Title)
            .SequenceEqual(library.Items.Select(b => (b.Title,b.Url,Path:library.PathName(b.FolderId))).OrderBy(b=>b.Title)) &&
            importedAgain.Folders.Select(f=>importedAgain.PathName(f.Id)).Order().SequenceEqual(library.Folders.Select(f=>library.PathName(f.Id)).Order()),
            "Exported Chrome HTML preserves folder paths including empty folders and duplicates in separate folders", "");
        var clip = library.Items.Single(b => b.Title == "第七集");
        bool edited = settings.EditBookmark(session,clip,"片段重命名",clip.Url,photos.Id) &&
            settings.EditBookmarkFolder(session,photos.Id,folder.Id,"图像") &&
            !settings.EditBookmarkFolder(session,folder.Id,photos.Id,"循环") &&
            settings.EditBookmarkFolder(session,null,photos.Id,"新建");
        var moved = settings.GetBookmarkLibrary();
        var changed = moved.Items.Single(b=>b.Title=="片段重命名");
        report(edited && moved.PathName(changed.FolderId) == "视频 › 图像" && moved.Folders.Any(f=>f.ParentId==photos.Id && f.Name=="新建") &&
            settings.RemoveBookmarkEntry(session,moved.Folders.Single(f=>f.Id==photos.Id)) && settings.IsBookmarked(clip.Url),
            "Folder creation, rename, move, cycle rejection and subtree removal preserve other copies of the URL", "");
        settings.SetBookmarkWorkspace(true);
        var decoy = settings.ActiveBookmarkSession!;
        bool isolated = settings.GetBookmarkLibrary().Folders.Count == 0 && settings.EditBookmarkFolder(decoy,null,"","临时") &&
            !settings.EditBookmarkFolder(session,null,"","过期") && !settings.ImportBookmarkLibrary(parsed,decoy,default,out _,out _);
        settings.SetBookmarkWorkspace(null);
        settings.SetBookmarkWorkspace(true);
        isolated &= settings.GetBookmarkLibrary().Folders.Count == 0;
        settings.SetBookmarkWorkspace(false);
        report(isolated && !settings.GetBookmarkLibrary().Folders.Any(f=>f.Name is "临时" or "过期") &&
            settings.GetBookmarkLibrary().Folders.Any(f=>f.Name=="视频"), "Decoy folders expire on lock and revoked bookmark sessions cannot import or change the real library", "");
    }
}
