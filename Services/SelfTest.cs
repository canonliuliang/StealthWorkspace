using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using StealthWorkspace.Models;

namespace StealthWorkspace.Services;
/// <summary>
/// Headless diagnostic runner (`StealthWorkspace.exe --self-test [outfile]`).
/// It exercises the security-critical pipeline (AES-GCM v2, legacy V1 read
/// compatibility, random-access streaming decryption, shredding, dual-vault unlock,
/// password rotation, corruption handling) without starting XAML. This exists because
/// the previous build had no automated way to prove the vault still worked.
/// </summary>
public static class SelfTest
{
    public static bool IsSelfTestRequested(string[] args)
    {
        return args != null && args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSmokeTestRequested(string[] args)
    {
        return args != null && args.Any(a => string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsUiSmokeRequested(string[] args)
    {
        return args != null && args.Any(a => string.Equals(a, "--ui-smoke", StringComparison.OrdinalIgnoreCase));
    }

    public static string? GetUiSmokeOutputPath(string[] args) => GetArgumentValue(args, "--ui-smoke");

    public static string? GetDataRootArgument(string[] args) => GetArgumentValue(args, "--data-root");

    public static string? GetSmokeTestOutputPath(string[] args) => GetArgumentValue(args, "--smoke-test");

    internal static string? GetArgumentValue(string[] args, string name)
    {
        if (args == null) return null;
        int index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            try { return Path.GetFullPath(args[index + 1]); }
            catch { return null; }
        }
        return null;
    }

    public static int Run(string[] args)
    {
        try
        {
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            TryWriteResult(args, $"SELFTEST FATAL: {ex.GetType().Name}: {ex.StackTrace}");
            return 2;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var results = new List<string>();
        var failures = new List<string>();
        int skipped = 0;
        string baseDir = GetOutputPath(args) is { } outPath
            ? Path.GetDirectoryName(outPath) ?? AppContext.BaseDirectory
            : AppContext.BaseDirectory;
        string testRoot = Path.Combine(baseDir, "selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testRoot);
        AppPaths.SetDataRootOverride(Path.Combine(testRoot, "diagnostics"));

        void Report(bool ok, string name, string detail = "")
        {
            string line = $"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  |  " + detail : string.Empty)}";
            results.Add(line);
            if (!ok) failures.Add(name);
            try { Console.WriteLine(line); } catch { }
        }

        void Skip(string name, string detail)
        {
            string line = $"SKIP  {name}  |  {detail}";
            results.Add(line);
            skipped++;
            try { Console.WriteLine(line); } catch { }
        }

        try
        {
            CollectionSelfTest.Run(Report);
            await VaultIntegritySelfTest.RunAsync(testRoot, Report, Skip);
            await LegacyVaultMigrationSelfTest.RunAsync(testRoot, Report);
            SettingsMigrationSelfTest.Run(testRoot, Report);
            var logWatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) AppLog.Info("Isolated logging performance fixture " + i);
            logWatch.Stop();
            Report(true, "Logging caller time for 100 records", $"{logWatch.Elapsed.TotalMilliseconds:F1} ms");
            bool flushed = await AppLog.FlushAsync(TimeSpan.FromSeconds(5));
            string diagnosticLog = AppLog.ReadAll();
            Report(flushed && Enumerable.Range(0, 100).All(i => diagnosticLog.Contains("Isolated logging performance fixture " + i + Environment.NewLine)),
                "Queued diagnostics flush and decrypt without losing accepted records");
            Report(!File.ReadAllText(AppPaths.LogPath).Contains("Isolated logging performance fixture"), "Queued diagnostics remain DPAPI encrypted on disk");
            try { throw new IOException("Private fixture C:\\private-example\\secret.jpg https://private.invalid/?token=secret"); }
            catch (Exception ex) { AppLog.Warn("Isolated privacy warning", ex); AppLog.Error("Isolated privacy error", ex); }
            await AppLog.FlushAsync(TimeSpan.FromSeconds(5));
            string privacyLog = AppLog.ReadAll();
            Report(privacyLog.Contains("Isolated privacy warning | IOException | 0x") &&
                privacyLog.Contains("Isolated privacy error | IOException | 0x") &&
                !privacyLog.Contains("private-example") && !privacyLog.Contains("private.invalid") &&
                !privacyLog.Contains("SelfTest.cs"), "Exception diagnostics omit private messages and source paths");
            double blockedLogCallerMs;
            using (var heldLog = new FileStream(AppPaths.LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                logWatch.Restart();
                for (int i = 0; i < 50; i++) AppLog.Info("Isolated locked-file fixture " + i);
                blockedLogCallerMs = logWatch.Elapsed.TotalMilliseconds;
                await AppLog.FlushAsync(TimeSpan.FromSeconds(5));
            }
            Report(blockedLogCallerMs < 500, "Locked log file does not block its caller", $"50 records: {blockedLogCallerMs:F1} ms");
            AppLog.Info("Logging recovered after sharing violation");
            await AppLog.FlushAsync(TimeSpan.FromSeconds(5));
            Report(AppLog.ReadAll().Contains("Logging recovered after sharing violation"), "Diagnostic writer recovers after a locked file");
            BrowserProfileSafetyTests.Run(testRoot, Report);
            Report(VaultImageDecoder.GetDecodeSize(800, 12000, 16384, 16 * 1024 * 1024) == (800u, 12000u),
                "Long image preserves original readable width");
            var hugeImageSize = VaultImageDecoder.GetDecodeSize(40000, 30000, 16384, 16 * 1024 * 1024);
            Report((ulong)hugeImageSize.Width * hugeImageSize.Height <= 16 * 1024 * 1024 &&
                Math.Abs(hugeImageSize.Width / (double)hugeImageSize.Height - 4.0 / 3) < .001,
                "Huge image decode remains within 64 MiB pixel budget and preserves aspect");
            Report(VaultImageDecoder.GetDecodeSize(800, 12000, 400, 16 * 1024 * 1024).Height == 400,
                "Gallery thumbnails stay small for long images");
            using (var reservedOne = BrowserDownloadPath.Reserve(testRoot, "same-name.bin"))
            using (var reservedTwo = BrowserDownloadPath.Reserve(testRoot, "same-name.bin"))
            {
                Report(reservedOne.FilePath != reservedTwo.FilePath, "Concurrent downloads reserve distinct names before file creation");
                File.WriteAllText(reservedOne.FilePath, "keep existing bytes");
            }
            using (var afterExisting = BrowserDownloadPath.Reserve(testRoot, "same-name.bin"))
                Report(Path.GetFileName(afterExisting.FilePath) != "same-name.bin" && File.ReadAllText(Path.Combine(testRoot, "same-name.bin")) == "keep existing bytes",
                    "Download reservation preserves an existing file");
            foreach (string unsafeName in new[] { "CON.txt", "..", "NUL", "x/y\\z.bin", new string('a', 180) + ".jpg" })
            {
                using var reserved = BrowserDownloadPath.Reserve(testRoot, unsafeName);
                try { File.WriteAllText(reserved.FilePath, "fixture"); Report(File.Exists(reserved.FilePath), "Download name accepted by Windows: " + unsafeName); }
                catch (Exception ex) { Report(false, "Download name accepted by Windows: " + unsafeName, ex.GetType().Name); }
            }
            foreach (var (domain, host, expected) in new (string, string, bool)[] {
                ("example.com", "example.com", true), (".example.com", "sub.example.com", true),
                (".example.com", "EXAMPLE.COM", true), ("example.com", "sub.example.com", false),
                ("notexample.com", "example.com", false), ("example.com.evil.test", "example.com", false),
                (".example.com", "notexample.com", false), (".sub.example.com", "example.com", false),
                ("localhost", "localhost", true), ("", "example.com", false)
            }) Report(BrowserSiteData.CookieBelongsToHost(domain, host) == expected, "Cookie host boundary: " + domain + " / " + host);
            // ---- 1. V2 encryption round trip + authenticated streaming ----
            string Search(string value) => "https://search.invalid/?q=" + Uri.EscapeDataString(value);
            var suggestionBookmarks = new[] { new BookmarkItem { Title = "图片索引", Url = "https://example.test/gallery" }, new BookmarkItem { Title = "unsafe", Url = "javascript:alert(1)" } };
            var suggestionHistory = new[] { new HistorySessionItem { Title = "最近浏览图片", Url = "https://example.test/gallery" } };
            var numericSuggestion = BrowserAddressSuggestions.Get("9", suggestionBookmarks, suggestionHistory, Search).First();
            Report(numericSuggestion.Target == Search("9") && numericSuggestion.Title.Contains("搜索"), "Omnibox explicitly labels a number as search");
            var matchedHistory = BrowserAddressSuggestions.Get("最近", suggestionBookmarks, suggestionHistory, Search);
            Report(matchedHistory.Any(s => s.Target == "https://example.test/gallery"), "Matching history survives a non-matching bookmark with the same URL");
            var allSuggestions = BrowserAddressSuggestions.Get("", suggestionBookmarks, suggestionHistory, Search);
            Report(allSuggestions.Count == 1 && allSuggestions[0].Target == "https://example.test/gallery", "Omnibox deduplicates local results and rejects script URLs");
            int searchUrlsBuilt = 0;
            string CountedSearch(string value) { searchUrlsBuilt++; return Search(value); }
            BrowserAddressSuggestions.Get("https://example.test/", suggestionBookmarks, suggestionHistory, CountedSearch);
            BrowserAddressSuggestions.Get("ordinary words", suggestionBookmarks, suggestionHistory, CountedSearch);
            Report(searchUrlsBuilt == 1, "Omnibox constructs a search URL only for the search suggestion");
            var edit = new BrowserAddressSuggestions.EditSession();
            var editBookmarks = suggestionBookmarks.ToImmutableList();
            BrowserAddressSuggestions.Get("会议", editBookmarks, suggestionHistory, Search, edit);
            var arrivingHistory = new[] { new HistorySessionItem { Title = "会议纪要", Url = "https://example.test/meeting" } };
            Report(BrowserAddressSuggestions.Get("会议纪", editBookmarks, arrivingHistory, Search, edit).Any(s => s.Target == arrivingHistory[0].Url) &&
                !BrowserAddressSuggestions.Get("会议纪", editBookmarks, [], Search, edit).Any(s => s.Target == arrivingHistory[0].Url),
                "An extended bookmark miss still follows added and cleared session history");
            var updatedBookmarks = editBookmarks.Add(new BookmarkItem { Title = "会议纪要", Url = "https://example.test/notes" });
            Report(BrowserAddressSuggestions.Get("会议纪", updatedBookmarks, [], Search, edit).Any(s => s.Target.EndsWith("/notes")) &&
                BrowserAddressSuggestions.Get("图", editBookmarks, [], Search, edit).Any(s => s.Target.EndsWith("/gallery")),
                "New bookmark snapshots and replaced input invalidate an earlier miss");
            Report(BrowserAddress.SamePage("HTTPS://EXAMPLE.TEST:443/Comic?id=ABC", "https://example.test/Comic?id=ABC"), "Bookmark canonical host and default port are equivalent");
            Report(!BrowserAddress.SamePage("https://example.test/Comic", "https://example.test/comic"), "Bookmarks preserve case-sensitive paths");
            Report(!BrowserAddress.SamePage("https://example.test/?id=ABC", "https://example.test/?id=abc"), "Bookmarks preserve case-sensitive query values");
            var bookmarkSettings = new SettingsService();
            var originalBookmarks = bookmarkSettings.Data.Bookmarks;
            bookmarkSettings.Data.Bookmarks = [.. Enumerable.Range(0, 10000).Select(index => new BookmarkItem { Url = $"https://example.test/page/{index}" }),
                new BookmarkItem { Url = "https://bücher.test/Comic?id=A#B" }];
            const string lookup = "https://xn--bcher-kva.test/Comic?id=A#B";
            var parsedLookup = new Uri(lookup);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            bool found = true;
            for (int iteration = 0; iteration < 40; iteration++)
                found &= bookmarkSettings.Data.Bookmarks.Any(item => BrowserAddress.SamePage(parsedLookup, item.Url));
            double scanMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            for (int iteration = 0; iteration < 40; iteration++) found &= bookmarkSettings.IsBookmarked(lookup);
            double indexedMs = timer.Elapsed.TotalMilliseconds;
            Report(found && !bookmarkSettings.IsBookmarked(lookup.ToLowerInvariant()),
                $"Bookmark lookup, 10001 entries x 40 (including index build): scan {scanMs:F2} ms; index {indexedMs:F2} ms");
            bookmarkSettings.Data.Bookmarks = bookmarkSettings.Data.Bookmarks.RemoveAt(10000);
            Report(!bookmarkSettings.IsBookmarked(lookup), "Bookmark index follows a replaced snapshot");
            bookmarkSettings.Data.Bookmarks = originalBookmarks;
            bookmarkSettings.AddBookmark("Upper", "https://example.test/Comic");
            bookmarkSettings.AddBookmark("Lower", "https://example.test/comic");
            bookmarkSettings.RenameBookmark("https://EXAMPLE.TEST/Comic", "Renamed upper");
            bookmarkSettings.RemoveBookmark("https://example.test/comic");
            var persistedBookmarks = new SettingsService().Data.Bookmarks;
            Report(persistedBookmarks.Count == 1 && persistedBookmarks[0].Title == "Renamed upper" && persistedBookmarks[0].Url == "https://example.test/Comic",
                "Rename and remove persist without deleting a differently cased bookmark");
            using (var heldSettings = new FileStream(AppPaths.SettingsPath + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Report(!bookmarkSettings.AddBookmark("Unsaved", "https://example.test/unsaved") && !bookmarkSettings.IsBookmarked("https://example.test/unsaved"),
                    "Failed bookmark add does not appear saved in memory");
                Report(!bookmarkSettings.RemoveBookmark("https://example.test/Comic") && bookmarkSettings.IsBookmarked("https://example.test/Comic"),
                    "Failed bookmark delete preserves the original entry");
                Report(!bookmarkSettings.RenameBookmark("https://example.test/Comic", "Unsaved name") && bookmarkSettings.Data.Bookmarks[0].Title == "Renamed upper",
                    "Failed bookmark rename preserves the original name");
            }
            int refreshedViews = 0;
            bookmarkSettings.SettingsChanged += () => throw new InvalidOperationException("Synthetic closed view");
            bookmarkSettings.SettingsChanged += () => refreshedViews++;
            Report(bookmarkSettings.AddBookmark("Saved", "https://example.test/saved") && refreshedViews == 1 &&
                new SettingsService().IsBookmarked("https://example.test/saved"), "A failed UI observer cannot undo a persisted bookmark or skip later observers");
            BrowserSessionHistory.Record("Loading", "https://example.test/A");
            var historyTime = BrowserSessionHistory.Items[0].VisitedAt;
            BrowserSessionHistory.Record("Other", "https://example.test/B");
            BrowserSessionHistory.UpdateTitle("Ready", "https://example.test/A");
            Report(BrowserSessionHistory.Items[1].Title == "Ready" && BrowserSessionHistory.Items[1].VisitedAt == historyTime,
                "Late document title does not reorder session history");
            BrowserSessionHistory.RememberClosed("First", "https://example.test/A", 1);
            BrowserSessionHistory.RememberClosed("Second", "https://example.test/A", 0);
            BrowserSessionHistory.RememberClosed("Lowercase path", "https://example.test/a", 3);
            BrowserSessionHistory.Forget(BrowserSessionHistory.Items[1]);
            Report(BrowserSessionHistory.RecentlyClosed.Count == 1 && BrowserSessionHistory.RecentlyClosed[0].Position == 3 &&
                BrowserSessionHistory.RecentlyClosed[0].Url == "https://example.test/a",
                "Forgetting one visit removes all matching closed tabs and preserves distinct case-sensitive paths");
            for (int i = 0; i < 25; i++)
                BrowserSessionHistory.RememberClosed("Synthetic", "https://example.test/" + i, i);
            BrowserSessionHistory.RememberClosed("Internal", "about:blank", 0);
            Report(BrowserSessionHistory.RecentlyClosed.Count == 20 && BrowserSessionHistory.RecentlyClosed[0].Position == 24 &&
                BrowserSessionHistory.RecentlyClosed[^1].Position == 5, "Closed tabs retain only the newest twenty web addresses");
            BrowserSessionHistory.Clear();
            BrowserSessionHistory.UpdateTitle("Too late", "https://example.test/A");
            Report(BrowserSessionHistory.Items.Count == 0 && BrowserSessionHistory.RecentlyClosed.Count == 0,
                "Clearing session history erases tab recovery and ignores late title changes");
            foreach (var (input, expected) in new (string, string?)[] {
                ("localhost:8768/path?q=1", "http://localhost:8768/path?q=1"),
                ("127.0.0.1:8080/", "http://127.0.0.1:8080/"),
                ("[::1]:8080/", "http://[::1]:8080/"),
                ("devbox:3000/path", "http://devbox:3000/path"),
                ("devbox:80/path", "http://devbox/path"),
                ("example.com:80/path", "http://example.com/path"),
                ("example.com/path", "https://example.com/path"),
                ("//example.com", "https://example.com/"),
                ("https://example.com/?q=a%20b", "https://example.com/?q=a%20b"),
                ("搜索 中文", Search("搜索 中文")),
                ("v1.2 release notes", Search("v1.2 release notes")),
                ("9", Search("9")), ("0", Search("0")), ("12345678", Search("12345678")),
                ("3.14", Search("3.14")), ("1.2.3", Search("1.2.3")), ("127.1", Search("127.1")),
                ("0x7f000001", Search("0x7f000001")), ("2130706433", Search("2130706433")),
                ("010.0.0.1", Search("010.0.0.1")), ("9:15", Search("9:15")),
                ("999.9.9.9", Search("999.9.9.9")), ("name@example.com", Search("name@example.com")),
                ("测试", Search("测试")), ("example.com", "https://example.com/"),
                ("  example.com  ", "https://example.com/"), ("localhost", "http://localhost/"),
                ("192.168.0.1/path", "http://192.168.0.1/path"), ("0.0.0.0:8080", "http://0.0.0.0:8080/"),
                ("例子.测试", "https://例子.测试/"),
                (@"C:\private\note.txt", null), (@"\\server\private\note.txt", null),
                ("javascript:alert(1)", null), ("file:///C:/secret.txt", null),
                ("data:text/html,hello", null), ("ftp://example.com/file", null), (" ", null)
            }) Report(BrowserAddress.Resolve(input, Search)?.AbsoluteUri == expected, "Address resolution: " + input);
            try
            {
                byte[] masterKey = CryptoService.GenerateRandomBytes(32);
                byte[] data = CryptoService.GenerateRandomBytes(3 * 1024 * 1024 + 700_000);
                string src = Path.Combine(testRoot, "sample.bin");
                string enc = Path.Combine(testRoot, "sample.svault");
                string dec = Path.Combine(testRoot, "sample.dec.bin");
                File.WriteAllBytes(src, data);

                CryptoService.EncryptFile(src, enc, masterKey);
                var header = CryptoService.ReadHeader(enc);
                Report(header.Format == VaultFileFormat.V2, "V2 header/format", header.Format.ToString());
                Report(header.PlainLength == data.Length, "V2 plain length", $"{header.PlainLength} == {data.Length}");
                Report(header.ChunkCount == 4, "V2 chunk count", header.ChunkCount.ToString());

                CryptoService.DecryptToFile(enc, dec, masterKey);
                Report(FilesEqual(src, dec), "V2 decrypt-to-file SHA256");

                using (var stream = CryptoService.OpenReadStream(enc, masterKey))
                {
                    bool randomAccessOk = true;
                    var rng = new Random(1234);
                    for (int i = 0; i < 64; i++)
                    {
                        long position = rng.NextInt64(0, data.Length);
                        int count = Math.Min(rng.Next(1, 200_000), data.Length - (int)position);
                        stream.Position = position;
                        byte[] buffer = new byte[count];
                        int read = stream.Read(buffer, 0, count);
                        if (read != count || !buffer.AsSpan(0, read).SequenceEqual(data.AsSpan((int)position, read)))
                        {
                            randomAccessOk = false;
                            break;
                        }
                    }
                    Report(randomAccessOk, "V2 random-access decrypt stream");
                }

                // Tamper detection: flipping a ciphertext byte must fail authentication.
                string tampered = Path.Combine(testRoot, "tampered.svault");
                byte[] encBytes = File.ReadAllBytes(enc);
                encBytes[CryptoService.HeaderSizeV2 + CryptoService.RecordHeaderSize + 100] ^= 0x5A;
                File.WriteAllBytes(tampered, encBytes);
                bool tamperRejected = false;
                try
                {
                    using var tamperedStream = CryptoService.OpenReadStream(tampered, masterKey);
                    byte[] buffer = new byte[4096];
                tamperedStream.ReadExactly(buffer);
                }
                catch (CryptographicException)
                {
                    tamperRejected = true;
                }
                Report(tamperRejected, "V2 chunk authentication (tamper rejected)");

                // Truncation must be rejected by the length header.
                string truncated = Path.Combine(testRoot, "truncated.svault");
                File.WriteAllBytes(truncated, encBytes.AsSpan(0, encBytes.Length - 5).ToArray());
                bool truncationRejected = false;
                try { CryptoService.ReadHeader(truncated); }
                catch (InvalidDataException) { truncationRejected = true; }
                Report(truncationRejected, "V2 truncation rejected");

                // Standalone re-encryption (password rotation primitive) must preserve content.
                byte[] rotatedKey = CryptoService.GenerateRandomBytes(32);
                string rotatedPath = Path.Combine(testRoot, "rotated.svault");
                string rotatedDecPath = Path.Combine(testRoot, "rotated.dec.bin");
                CryptoService.ReEncryptFile(enc, rotatedPath, masterKey, rotatedKey);
                CryptoService.DecryptToFile(rotatedPath, rotatedDecPath, rotatedKey);
                Report(File.ReadAllBytes(rotatedDecPath).AsSpan().SequenceEqual(data), "V2 re-encryption content preserved");
            }
            catch (Exception ex)
            {
                Report(false, "V2 encryption pipeline", ex.GetType().Name + ": " + ex.Message);
            }

            // ---- 2. Legacy V1 read compatibility ----
            try
            {
                byte[] key = CryptoService.GenerateRandomBytes(32);
                byte[] data = CryptoService.GenerateRandomBytes(2 * 1024 * 1024 + 4321);
                string v1Path = Path.Combine(testRoot, "legacy.svault");
                string decPath = Path.Combine(testRoot, "legacy.dec.bin");
                WriteLegacyV1File(v1Path, data, key);

                var header = CryptoService.ReadHeader(v1Path);
                Report(header.Format == VaultFileFormat.LegacyV1, "V1 format detection");
                Report(header.PlainLength == data.Length, "V1 length scan", header.PlainLength.ToString());

                CryptoService.DecryptToFile(v1Path, decPath, key);
                Report(FilesEqual(v1Path, decPath) == false, "V1 decrypt writes output");
                Report(File.ReadAllBytes(decPath).AsSpan().SequenceEqual(data), "V1 decrypt content SHA256");

                using var stream = CryptoService.OpenReadStream(v1Path, key);
                stream.Position = 1024 * 1024 + 55;
                byte[] buffer = new byte[1000];
                int read = stream.Read(buffer, 0, buffer.Length);
                Report(read == buffer.Length && buffer.AsSpan(0, read).SequenceEqual(data.AsSpan(1024 * 1024 + 55, read)),
                    "V1 random-access decrypt stream");
                stream.Position = 0;
                byte[] full = new byte[data.Length];
                stream.ReadExactly(full);
                Report(full.AsSpan().SequenceEqual(data), "V1 stream reads through a short interior record");
            }
            catch (Exception ex)
            {
                Report(false, "V1 compatibility", ex.GetType().Name + ": " + ex.Message);
            }

            // ---- 3. Shredding ----
            try
            {
                string shred = Path.Combine(testRoot, "shred.bin");
                File.WriteAllBytes(shred, CryptoService.GenerateRandomBytes(256 * 1024));
                bool ok = CryptoService.ShredFile(shred, 1);
                Report(ok && !File.Exists(shred), "Secure shred");
            }
            catch (Exception ex)
            {
                Report(false, "Secure shred", ex.GetType().Name);
            }

            // ---- 4. Dual vault end-to-end ----
            try
            {
                string vaultRoot = Path.Combine(testRoot, "vault-data");
                Directory.CreateDirectory(vaultRoot);
                var vault = new VaultService(vaultRoot);
                Report(!vault.HasVault && !vault.IsCorrupted, "Empty vault detected");

                await vault.InitializeVaultAsync("Master#123456", "Decoy#123456");
                Report(vault.IsUnlocked && !vault.IsDecoyMode, "Vault initialized & unlocked");

                byte[] realData = CryptoService.GenerateRandomBytes(2 * 1024 * 1024 + 321);
                string realSrc = Path.Combine(testRoot, "real-media.bin");
                File.WriteAllBytes(realSrc, realData);
                VaultItem realItem = await vault.ImportFileAsync(realSrc, shredOriginal: false);
                Report(vault.VisibleItems.Count == 1, "Import into real vault");

                using (Stream stream = vault.OpenDecryptedStream(realItem))
                {
                    stream.Position = 1_500_000;
                    byte[] buffer = new byte[100_000];
                    int read = stream.Read(buffer, 0, buffer.Length);
                    Report(read == buffer.Length && buffer.AsSpan(0, read).SequenceEqual(realData.AsSpan(1_500_000, read)),
                        "Vault stream random access");
                }

                string exportPath = Path.Combine(testRoot, "exported.bin");
                await vault.ExportItemAsync(realItem, exportPath);
                Report(File.ReadAllBytes(exportPath).AsSpan().SequenceEqual(realData), "Vault export");

                vault.Lock();
                var wrong = vault.VerifyPassword("definitely-wrong");
                wrong.ForgetKey();
                Report(wrong.Status == VaultUnlockStatus.WrongPassword, "Wrong password rejected", wrong.Status.ToString());

                var realUnlock = vault.VerifyPassword("Master#123456");
                Report(vault.ApplyUnlock(realUnlock), "Master unlock");
                Report(vault.VisibleItems.Count == 1, "Real vault item visible");

                vault.Lock();
                var decoyUnlock = vault.VerifyPassword("Decoy#123456");
                Report(decoyUnlock.Status == VaultUnlockStatus.Success && decoyUnlock.IsDecoy, "Decoy password recognized");
                Report(vault.ApplyUnlock(decoyUnlock), "Decoy unlock");
                Report(vault.IsDecoyMode && vault.VisibleItems.Count == 0, "Decoy vault is separate/empty");

                byte[] decoyData = CryptoService.GenerateRandomBytes(1024 * 1024 + 7);
                string decoySrc = Path.Combine(testRoot, "decoy-media.bin");
                File.WriteAllBytes(decoySrc, decoyData);
                VaultItem decoyItem = await vault.ImportFileAsync(decoySrc, shredOriginal: false);
                Report(vault.VisibleItems.Count == 1, "Import into decoy vault");

                vault.Lock();
                var realUnlock2 = vault.VerifyPassword("Master#123456");
                vault.ApplyUnlock(realUnlock2);
                Report(!vault.IsDecoyMode && vault.VisibleItems.Count == 1 && vault.VisibleItems[0].Id != decoyItem.Id,
                    "Master view isolated from decoy items");

                using (Stream check = vault.OpenDecryptedStream(vault.VisibleItems[0]))
                {
                    check.Position = 777_777;
                    byte[] buffer = new byte[50_000];
                    int read = check.Read(buffer, 0, buffer.Length);
                    Report(read == buffer.Length && buffer.AsSpan(0, read).SequenceEqual(realData.AsSpan(777_777, read)),
                        "Pre-rotation stream still valid");
                }

                await vault.ChangeMasterPasswordAsync("NewMaster#123456");
                vault.Lock();
                var oldMaster = vault.VerifyPassword("Master#123456");
                oldMaster.ForgetKey();
                Report(oldMaster.Status == VaultUnlockStatus.WrongPassword, "Old master password rejected after rotation");
                var newMaster = vault.VerifyPassword("NewMaster#123456");
                vault.ApplyUnlock(newMaster);
                Report(vault.VisibleItems.Count == 1, "Vault still readable after password rotation");
                using (Stream stream = vault.OpenDecryptedStream(vault.VisibleItems[0]))
                {
                    byte[] buffer = new byte[200_000];
                    stream.Position = 1_000_000;
                    int read = stream.Read(buffer, 0, buffer.Length);
                    Report(read == buffer.Length && buffer.AsSpan(0, read).SequenceEqual(realData.AsSpan(1_000_000, read)),
                        "Rotated file still decrypts correctly");
                }

                await vault.SetDecoyPasswordAsync("NewDecoy#123456", oldDecoyPassword: "Decoy#123456");
                vault.Lock();
                var newDecoy = vault.VerifyPassword("NewDecoy#123456");
                Report(vault.ApplyUnlock(newDecoy) && vault.IsDecoyMode && vault.VisibleItems.Count == 1,
                    "Decoy rotation & unlock");
                using (Stream stream = vault.OpenDecryptedStream(vault.VisibleItems[0]))
                {
                    byte[] buffer = new byte[500_000];
                    int read = stream.Read(buffer, 0, buffer.Length);
                    Report(read == buffer.Length && buffer.AsSpan(0, read).SequenceEqual(decoyData.AsSpan(0, read)),
                        "Rotated decoy file still decrypts correctly");
                }

                vault.Lock();
                var realUnlock3 = vault.VerifyPassword("NewMaster#123456");
                vault.ApplyUnlock(realUnlock3);
                await vault.ClearDecoyAsync();
                Report(!vault.HasDecoyVault, "Decoy vault removed");

                await vault.DeleteItemAsync(vault.VisibleItems[0]);
                Report(vault.VisibleItems.Count == 0, "Delete item");

                await vault.ResetVaultAsync();
                Report(!vault.HasVault && !vault.IsCorrupted, "Reset vault");

                // Corruption must be detected as corruption (not offered as first-run setup).
                File.WriteAllBytes(Path.Combine(vaultRoot, "system_catalog.dat"), new byte[] { 1, 2, 3, 4, 5 });
                var corruptVault = new VaultService(vaultRoot);
                Report(corruptVault.IsCorrupted && corruptVault.HasVault, "Corrupt config detected safely");
            }
            catch (Exception ex)
            {
                Report(false, "Dual vault end-to-end", ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
            }
        }
        finally
        {
            try { await RemovalSelfTest.RunAsync(testRoot, Report, Skip); }
            catch (Exception ex) { Report(false, "Removal follow-up regression", ex.ToString()); }
            try { await AuditSelfTest.RunAsync(testRoot, Report); }
            catch (Exception ex) { Report(false, "Security audit regression", ex.ToString()); }
            try { await BackupSelfTest.RunAsync(testRoot, Report); }
            catch (Exception ex) { Report(false, "Backup/restore regression", ex.ToString()); }
            await AppLog.FlushAsync(TimeSpan.FromSeconds(5));
            try { Directory.Delete(testRoot, recursive: true); } catch { }
        }

        var summary = new StringBuilder();
        summary.AppendLine("StealthWorkspace self-test " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        summary.AppendLine($"Passed: {results.Count - failures.Count - skipped}  Failed: {failures.Count}  Skipped: {skipped}");
        summary.AppendLine(new string('-', 60));
        foreach (string line in results) summary.AppendLine(line);
        if (failures.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine("FAILED: " + string.Join(", ", failures));
        }

        TryWriteResult(args, summary.ToString());
        return failures.Count == 0 ? 0 : 1;
    }

    private static string? GetOutputPath(string[] args)
    {
        return GetArgumentValue(args, "--self-test")
               ?? SafeDefaultOutputPath();
    }

    private static string? SafeDefaultOutputPath()
    {
        try
        {
            return Path.Combine(Environment.CurrentDirectory, "selftest-result.txt");
        }
        catch
        {
            return null;
        }
    }

    private static void TryWriteResult(string[] args, string content)
    {
        try
        {
            string? path = GetOutputPath(args);
            if (string.IsNullOrEmpty(path)) return;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content, Encoding.UTF8);
        }
        catch
        {
            // ignored
        }
    }

    private static bool FilesEqual(string pathA, string pathB)
    {
        using var ha = SHA256.Create();
        using var hb = SHA256.Create();
        using var a = File.OpenRead(pathA);
        using var b = File.OpenRead(pathB);
        byte[] hashA = ha.ComputeHash(a);
        byte[] hashB = hb.ComputeHash(b);
        return hashA.AsSpan().SequenceEqual(hashB);
    }

    /// <summary>Writes a file using the legacy (V1) format so compatibility can be verified.</summary>
    private static void WriteLegacyV1File(string path, byte[] plaintext, byte[] key)
    {
        byte[] magic = Encoding.ASCII.GetBytes("SVAULT01");
        byte[] salt = CryptoService.GenerateRandomBytes(CryptoService.SaltSize);
        int chunkSize = CryptoService.DefaultChunkSize;

        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(output);
        writer.Write(magic);
        writer.Write(salt);
        writer.Write(chunkSize);

        using var aes = new AesGcm(key, CryptoService.TagSize);
        int offset = 0;
        while (offset < plaintext.Length)
        {
            int length = Math.Min(offset == 0 ? chunkSize / 2 : chunkSize, plaintext.Length - offset);
            byte[] nonce = CryptoService.GenerateRandomBytes(CryptoService.NonceSize);
            byte[] tag = new byte[CryptoService.TagSize];
            byte[] cipher = new byte[length];
            aes.Encrypt(nonce, plaintext.AsSpan(offset, length), cipher, tag);

            writer.Write(nonce);
            writer.Write(tag);
            writer.Write(length);
            writer.Write(cipher);
            offset += length;
        }
        writer.Flush();
    }
}

/// <summary>
/// Minimal end-to-end UI smoke test (`--smoke-test out.txt`): starts the real XAML
/// application, waits for the root visual tree and the lock page to load, records a few
/// observable facts, then closes the window and writes the result. Used by developers to
/// prove a build can actually boot WinUI (the headless self-test cannot cover XAML).
/// </summary>
public static class SmokeTest
{
    private static readonly object Gate = new();
    private static readonly List<string> Lines = new();

    public static bool Enabled { get; private set; }
    public static bool UiSmoke { get; private set; }
    public static string? OutputPath { get; private set; }
    public static bool Interactive { get; private set; }
    public static bool InteractiveFixture { get; private set; }
    public static bool ReduceMotion { get; private set; }
    public static int UiWidth { get; private set; } = 1100;
    public static int UiHeight { get; private set; } = 740;
    public static string? UiTheme { get; private set; }
    public static string? BrowserProbeUrl { get; private set; }
    public static bool ReferenceBrowserProbe { get; private set; }
    public static int BrowserSoakMinutes { get; private set; }
    public static bool ReopenMedia { get; private set; }
    public static bool ExperienceTest { get; private set; }
    public static bool ShutdownAudit { get; private set; }
    public static bool ShutdownDuringInitialization { get; private set; }
    public static bool BrowserAudit { get; private set; }
    public static bool RenderAudit { get; private set; }
    public static bool RecoveryAuditOnly { get; private set; }
    public static string? MediaFixtureRoot { get; private set; }
    public static string? SharedFixtureRoot { get; private set; }

    public static void Enable(string[] args)
    {
        Enabled = true;
        UiSmoke = SelfTest.IsUiSmokeRequested(args);
        Interactive = args.Contains("--ui-interactive");
        InteractiveFixture = UiSmoke && Interactive && args.Contains("--ui-fixture");
        ReduceMotion = UiSmoke && args.Contains("--ui-reduced-motion");
        if (UiSmoke)
        {
            if (int.TryParse(args.SkipWhile(a => a != "--ui-width").Skip(1).FirstOrDefault(), out int width)) UiWidth = Math.Clamp(width, 480, 2000);
            if (int.TryParse(args.SkipWhile(a => a != "--ui-height").Skip(1).FirstOrDefault(), out int height)) UiHeight = Math.Clamp(height, 480, 1600);
            UiTheme = args.SkipWhile(a => a != "--ui-theme").Skip(1).FirstOrDefault() is "Light" ? "Light" :
                args.SkipWhile(a => a != "--ui-theme").Skip(1).FirstOrDefault() is "Dark" ? "Dark" : null;
        }
        BrowserProbeUrl = args.SkipWhile(a => a != "--browser-probe-url").Skip(1).FirstOrDefault();
        ReferenceBrowserProbe = BrowserProbeUrl != null && args.Contains("--browser-reference-probe");
        if (int.TryParse(args.SkipWhile(a => a != "--browser-soak-minutes").Skip(1).FirstOrDefault(), out int minutes))
            BrowserSoakMinutes = Math.Clamp(minutes, 1, 120);
        ReopenMedia = args.Contains("--media-reopen");
        ExperienceTest = args.Contains("--experience-test");
        ShutdownDuringInitialization = args.Contains("--shutdown-audit-pending");
        ShutdownAudit = ShutdownDuringInitialization || args.Contains("--shutdown-audit");
        RecoveryAuditOnly = args.Contains("--browser-recovery-audit");
        BrowserAudit = RecoveryAuditOnly || args.Contains("--browser-audit");
        RenderAudit = args.Contains("--browser-render-audit");
        MediaFixtureRoot = SelfTest.GetArgumentValue(args, "--media-fixtures");
        SharedFixtureRoot = SelfTest.GetArgumentValue(args, "--shared-fixtures");
        OutputPath = (UiSmoke ? SelfTest.GetUiSmokeOutputPath(args) : SelfTest.GetSmokeTestOutputPath(args))
                     ?? Path.Combine(Environment.CurrentDirectory, UiSmoke ? "ui-smoke-result.txt" : "smoke-test-result.txt");
    }

    public static void Report(string line)
    {
        string entry = $"{DateTime.Now:HH:mm:ss.fff}  {line}";
        lock (Gate)
        {
            Lines.Add(entry);
        }

        // Flush every entry so a native crash during the UI walk still leaves evidence of
        // the last reached step.
        if (OutputPath == null) return;
        try
        {
            File.AppendAllText(OutputPath, entry + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // ignored
        }
    }

    public static void Write()
    {
        if (OutputPath == null) return;
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("StealthWorkspace UI smoke test" + (UiSmoke ? " (full page walk)" : string.Empty));
            bool anyUnhandled;
            lock (Gate)
            {
                foreach (string line in Lines) builder.AppendLine(line);
                anyUnhandled = Lines.Any(l => l.Contains("unhandled-ui-exception", StringComparison.Ordinal));
            }

            bool ready = false;
            lock (Gate)
            {
                ready = Lines.Any(l => l.Contains("UI ready", StringComparison.Ordinal));
            }

            bool pagesOk = !UiSmoke;
            if (UiSmoke)
            {
                string[] requiredPages = { "VaultPage", "BrowserPage", "CleanerPage", "SettingsPage", "PlayerPage", "LockPage" };
                pagesOk = requiredPages.All(page =>
                {
                    lock (Gate)
                    {
                        return Lines.Any(l => l.Contains("page-visited: " + page, StringComparison.Ordinal));
                    }
                });
            }

            bool webViewOk = true;
            if (UiSmoke)
            {
                lock (Gate)
                {
                    webViewOk = Lines.Any(l => l.Contains("webview-ready", StringComparison.Ordinal));
                }
            }

            bool completed = false;
            lock (Gate)
            {
                completed = Lines.Any(l => l.Contains("ui-smoke complete", StringComparison.Ordinal));
            }

            bool ok = ready && !anyUnhandled && pagesOk && webViewOk && (!UiSmoke || completed);
            builder.AppendLine(ShutdownAudit ? "SHUTDOWN AUDIT: " + (Lines.Any(l => (l.Contains("shutdown audit: live WebView", StringComparison.Ordinal) || l.Contains("shutdown audit: initializing WebView", StringComparison.Ordinal))) && Lines.Any(l => l.Contains("closing: vault locked=True", StringComparison.Ordinal)) && Lines.Any(l => l.Contains("closing: browser profiles removed=True", StringComparison.Ordinal)) && !anyUnhandled ? "OK" : "FAILED") : RenderAudit ? "RENDER AUDIT: " + (Lines.Any(l => l.Contains("render audit complete", StringComparison.Ordinal)) && !anyUnhandled ? "OK" : "FAILED") : BrowserAudit ? "AUDIT RESULT: " + (Lines.Any(l => l.Contains("audit complete", StringComparison.Ordinal)) && !anyUnhandled ? "OK" : "FAILED") : ExperienceTest ? "EXPERIENCE RESULT: " + (Lines.Any(l => l.Contains("experience complete", StringComparison.Ordinal)) && !anyUnhandled ? "OK" : "FAILED") : BrowserSoakMinutes > 0 ? "SOAK RESULT: " + (Lines.Any(l => l.Contains("soak complete", StringComparison.Ordinal)) && !anyUnhandled ? "OK" : "FAILED") : BrowserProbeUrl != null ? "NETWORK PROBE: completed; inspect metrics separately" : Interactive ? "MANUAL SESSION: completed; inspect UI evidence separately" : "SMOKE RESULT: " + (ok ? "OK" : "FAILED"));
            string? dir = Path.GetDirectoryName(OutputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(OutputPath, builder.ToString(), Encoding.UTF8);
        }
        catch
        {
            // ignored
        }
    }
}
