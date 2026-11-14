namespace StealthWorkspace.Services;

internal static class BrowserProfileSafetyTests
{
    internal static void Run(string testRoot, Action<bool, string, string> report)
    {
        string root = Path.Combine(testRoot, "profile-cleanup");
        Directory.CreateDirectory(root);
        string Create(bool marker, bool old = true)
        {
            string path = Path.Combine(root, "Stealth_WebProfile_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "fixture-data"), "synthetic test profile");
            if (marker) File.WriteAllText(Path.Combine(path, ".stealth-session-owner"), "");
            if (old) Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
            return path;
        }
        string live = Create(true);
        using (new FileStream(Path.Combine(live, ".stealth-session-owner"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            report(!BrowserProfileManager.TryDeleteAbandonedProfile(live, root) && Directory.Exists(live),
                "Profile cleanup preserves an old profile with a live owner", "");
        report(BrowserProfileManager.TryDeleteAbandonedProfile(live, root) && !Directory.Exists(live),
            "Profile cleanup removes an abandoned marked profile", "");
        string legacy = Create(false);
        report(!BrowserProfileManager.TryDeleteAbandonedProfile(legacy, root) && Directory.Exists(legacy),
            "Profile cleanup preserves unmarked legacy profiles", "");
        string young = Create(true, false);
        report(!BrowserProfileManager.TryDeleteAbandonedProfile(young, root) && Directory.Exists(young),
            "Profile cleanup preserves a recent profile", "");
        string outside = Create(true);
        report(!BrowserProfileManager.TryDeleteAbandonedProfile(outside, Path.Combine(root, "different-root")) && Directory.Exists(outside),
            "Profile cleanup rejects a path outside the specified root", "");
        string unrelated = Path.Combine(root, "Stealth_WebProfile_not-a-guid");
        Directory.Move(outside, unrelated);
        report(!BrowserProfileManager.TryDeleteAbandonedProfile(unrelated, root) && Directory.Exists(unrelated),
            "Profile cleanup rejects a non-session directory", "");
    }
}
