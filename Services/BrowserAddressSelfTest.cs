using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using StealthWorkspace.Models;

namespace StealthWorkspace.Services;

internal static class BrowserAddressSelfTest
{
    internal static void Run(Action<bool, string, string> report, string scratch)
    {
        string root = Directory.CreateDirectory(Path.Combine(scratch, "bookmark-workspace-" + Guid.NewGuid().ToString("N"))).FullName;
        string path = Path.Combine(root, "settings.dat");
        File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes("""
            {"BrowserFirstExperience":true,"Bookmarks":[{"Title":"Personal title","Url":"https://example.test/Case"}]}
            """), null, DataProtectionScope.CurrentUser));
        var settings = new SettingsService(root, null);
        const string shared = "https://example.test/Case";
        var personal = settings.PersonalBookmarkSession;
        report(settings.CurrentBookmarks.Single().Title == "Personal title" && settings.IsBookmarked(shared),
            "Pre-workspace bookmarks reopen in the personal space", "");

        string digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        settings.SetBookmarkWorkspace(true);
        bool isolated = settings.CurrentBookmarks.Count == 0 && !settings.IsBookmarked(shared) &&
            settings.AddBookmark("Temporary title", shared) && settings.RenameBookmark(shared, "Session only");
        var rows = BrowserAddressSuggestions.Get("only", settings.CurrentBookmarks, [],
            s => "https://search.invalid/?q=" + Uri.EscapeDataString(s));
        isolated &= rows.Any(row => row.Title == "Session only") && settings.RemoveBookmark(shared) &&
            settings.AddBookmark("Next unlock must forget", "https://decoy.invalid/page") &&
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == digest;
        report(isolated, "Temporary bookmarks, star and suggestions stay separate without writing settings", "");
        settings.SetBookmarkWorkspace(null);
        bool locked = settings.CurrentBookmarks.Count == 0 && !settings.AddBookmark("Locked", shared) &&
            !settings.ImportBookmarks([new BookmarkItem { Url = shared }], out _);
        settings.SetBookmarkWorkspace(true);
        bool cleared = settings.CurrentBookmarks.Count == 0;
        settings.SetBookmarkWorkspace(false);
        report(locked && cleared && settings.CurrentBookmarks.Single().Title == "Personal title" &&
            !settings.ImportBookmarks([new BookmarkItem { Url = "https://wrong-session.invalid" }], out _, default, personal),
            "Lock revokes pending imports and discards temporary bookmarks while preserving the personal space", "");

        var incoming = new[]
        {
            new BookmarkItem { Title = "duplicate", Url = "https://EXAMPLE.test:443/Case" },
            new BookmarkItem { Title = "Path case", Url = "https://example.test/case" },
            new BookmarkItem { Title = "IDN", Url = "https://bücher.test/a" },
            new BookmarkItem { Title = "IDN alias", Url = "https://xn--bcher-kva.test/a" },
            new BookmarkItem { Url = "file:///C:/fixture" },
            new BookmarkItem { Url = "https://name:test@host.invalid/" }
        };
        bool imported = settings.ImportBookmarks(incoming, out int count) && count == 2;
        report(imported && settings.ImportBookmarks(incoming, out int again) && again == 0 &&
            new SettingsService(root, null).CurrentBookmarks.Select(b => b.Title).SequenceEqual(["Path case", "IDN", "Personal title"]),
            "Bookmark import preserves order and URL identity, skips credentials, and is idempotent on disk", "");

        var pending = new[] { new BookmarkItem { Url = "https://pending.invalid/" } };
        using (var held = new FileStream(path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            report(!settings.ImportBookmarks(pending, out int saved) && saved == 0 && !settings.IsBookmarked(pending[0].Url),
                "Failed batch import rolls back both settings and membership", "");
        try
        {
            settings.ImportBookmarks(pending, out _, new CancellationToken(canceled: true));
            report(false, "Cancelled bookmark import leaves the original snapshot", "Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            report(!settings.IsBookmarked(pending[0].Url), "Cancelled bookmark import leaves the original snapshot", "");
        }
        Parallel.Invoke(
            () => settings.ImportBookmarks([new BookmarkItem { Url = "https://race-a.invalid/" }], out _),
            () => settings.ImportBookmarks([new BookmarkItem { Url = "https://race-b.invalid/" }], out _));
        var persisted = new SettingsService(root, null);
        report(persisted.IsBookmarked("https://race-a.invalid/") && persisted.IsBookmarked("https://race-b.invalid/"),
            "Concurrent bookmark imports retain both committed batches", "");

        settings.Data.Bookmarks = [.. Enumerable.Range(0, 50).Select(i => new BookmarkItem { Url = $"https://large.invalid/{i}" }),
            new BookmarkItem { Title = "First", Url = shared }, new BookmarkItem { Title = "Second", Url = "https://EXAMPLE.test:443/Case" }];
        bool duplicates = settings.IsBookmarked(shared) && settings.RenameBookmark(shared, "First changed") &&
            settings.CurrentBookmarks[^2].Title == "First changed" && settings.CurrentBookmarks[^1].Title == "Second" &&
            settings.RemoveBookmark(shared) && !settings.IsBookmarked(shared);
        report(duplicates, "Indexed edits preserve legacy duplicate rename and delete semantics", "");

        int searches = 0;
        string Search(string input) { searches++; return "https://search.invalid/?q=" + Uri.EscapeDataString(input); }
        string[] sensitive = ["fixture:password@example.test", "fixture:password@example.test:not-a-port", "tel:+15550100", "sms:+15550100?body=test",
            "ms-settings:privacy-location", "otpauth:fixture", "magnet:?xt=test"];
        bool prevented = sensitive.All(s => BrowserAddress.Resolve(s, Search) == null);
        report(prevented && searches == 0, "Pasted password authorities and local protocol handlers cannot become remote searches", "");
        string[] normal = ["site:example.test notes", "site:example.test", "intitle:project", "a@example.test", "notes: draft"];
        report(normal.All(s => BrowserAddress.Resolve(s, Search, out bool search) != null && search) && searches == normal.Length &&
            BrowserAddress.Resolve("localhost:8080", Search)?.Port == 8080, "Search operators, email searches and local development ports still work", "");

        var edit = new BrowserAddressSuggestions.EditSession();
        var bookmarks = ImmutableList.Create(new BookmarkItem { Title = "稀有标题", Url = shared });
        var history = new List<HistorySessionItem>();
        BrowserAddressSuggestions.Get("稀有", bookmarks, history, Search, edit);
        history.Add(new HistorySessionItem { Title = "稀有标题二", Url = "https://history.invalid/" });
        var newer = bookmarks.Add(new BookmarkItem { Title = "稀有标题三", Url = "https://added.invalid/" });
        var latest = BrowserAddressSuggestions.Get("稀有标题", newer, history, Search, edit);
        var narrowed = BrowserAddressSuggestions.Get("稀有标题二", newer, history, Search, edit);
        report(latest.Any(s => s.Target == shared) && latest.Any(s => s.Target == "https://added.invalid/") &&
            narrowed.Any(s => s.Target == "https://history.invalid/"), "Narrowing suggestions observes new bookmark versions and live history", "");
    }
}
