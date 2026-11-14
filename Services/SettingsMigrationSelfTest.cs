using System.Text;
using System.Text.Json;

namespace StealthWorkspace.Services;

internal static class SettingsMigrationSelfTest
{
    internal static void Run(string testRoot, Action<bool, string, string> report)
    {
        string root = Path.Combine(testRoot, "settings-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string legacyUrl = "https://legacy.fixture.invalid/Retained?id=ABC";
        string valid = JsonSerializer.Serialize(new AppSettingsData
        {
            BrowserFirstExperience = true,
            AppTheme = "Light",
            Bookmarks = [new BookmarkItem { Title = "Retained legacy bookmark", Url = legacyUrl }]
        });
        (string Data, string Legacy) Fixture(string name, string text)
        {
            string data = Path.Combine(root, name, "current");
            string legacy = Path.Combine(root, name, "legacy", "settings.json");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
            File.WriteAllText(legacy, text, Encoding.UTF8);
            return (data, legacy);
        }
        static void Require(bool value) { if (!value) throw new InvalidOperationException("Settings migration assertion failed"); }
        void Check(string name, Action check)
        {
            try { check(); report(true, "Settings migration: " + name, ""); }
            catch (Exception ex) { report(false, "Settings migration: " + name, ex.GetType().Name); }
        }

        Check("failed write preserves source and retries after the obstruction is removed", () =>
        {
            var fixture = Fixture("blocked-write", valid);
            string target = Path.Combine(fixture.Data, "settings.dat");
            byte[] before = File.ReadAllBytes(fixture.Legacy);
            using (var blocker = new FileStream(target + ".tmp", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                _ = new SettingsService(fixture.Data, fixture.Legacy);
                Require(File.ReadAllBytes(fixture.Legacy).AsSpan().SequenceEqual(before) && !File.Exists(target));
            }
            var recovered = new SettingsService(fixture.Data, fixture.Legacy);
            Require(recovered.Data.AppTheme == "Light" && recovered.IsBookmarked(legacyUrl));
            Require(!File.Exists(fixture.Legacy) && File.Exists(target));
            Require(new SettingsService(fixture.Data, fixture.Legacy).IsBookmarked(legacyUrl));
            Require(!Encoding.UTF8.GetString(File.ReadAllBytes(target)).Contains(legacyUrl));
        });
        Check("malformed source remains intact across subsequent launches", () =>
        {
            var fixture = Fixture("malformed", "{\"Bookmarks\": [ incomplete but recoverable");
            byte[] before = File.ReadAllBytes(fixture.Legacy);
            _ = new SettingsService(fixture.Data, fixture.Legacy);
            _ = new SettingsService(fixture.Data, fixture.Legacy);
            Require(File.ReadAllBytes(fixture.Legacy).AsSpan().SequenceEqual(before));
        });
        Check("unreadable encrypted destination cannot discard the readable legacy source", () =>
        {
            var fixture = Fixture("corrupt-destination", valid);
            File.WriteAllBytes(Path.Combine(fixture.Data, "settings.dat"), [1, 2, 3, 4]);
            var failed = new SettingsService(fixture.Data, fixture.Legacy);
            Require(failed.LoadFailed && File.Exists(fixture.Legacy));
            Require(Directory.EnumerateFiles(fixture.Data, "settings.corrupt-*.dat").Any());
            var recovered = new SettingsService(fixture.Data, fixture.Legacy);
            Require(recovered.IsBookmarked(legacyUrl) && !File.Exists(fixture.Legacy));
        });
        Check("existing valid settings remain authoritative", () =>
        {
            var fixture = Fixture("existing-current", valid);
            string absentLegacy = Path.Combine(root, "does-not-exist.json");
            var current = new SettingsService(fixture.Data, absentLegacy);
            Require(current.AddBookmark("Current", "https://current.fixture.invalid/"));
            var migrated = new SettingsService(fixture.Data, fixture.Legacy);
            Require(migrated.IsBookmarked("https://current.fixture.invalid/") && !migrated.IsBookmarked(legacyUrl));
            Require(!File.Exists(fixture.Legacy));
        });
        Check("blocked source cleanup leaves a usable encrypted copy and retries safely", () =>
        {
            var fixture = Fixture("blocked-cleanup", valid);
            using (var reader = new FileStream(fixture.Legacy, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var migrated = new SettingsService(fixture.Data, fixture.Legacy);
                Require(migrated.IsBookmarked(legacyUrl) && File.Exists(fixture.Legacy));
                Require(File.Exists(Path.Combine(fixture.Data, "settings.dat")));
            }
            Require(new SettingsService(fixture.Data, fixture.Legacy).IsBookmarked(legacyUrl));
            Require(!File.Exists(fixture.Legacy));
        });
    }
}
