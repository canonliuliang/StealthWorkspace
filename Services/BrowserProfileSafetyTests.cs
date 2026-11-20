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
        string interrupted = Create(true);
        string markerPath = Path.Combine(interrupted, ".stealth-session-owner");
        using (new FileStream(Path.Combine(interrupted, "fixture-data"), FileMode.Open, FileAccess.Read, FileShare.None))
            report(!BrowserProfileManager.TryDeleteAbandonedProfile(interrupted, root) && File.Exists(markerPath),
                "Interrupted profile cleanup preserves the ownership marker for a future startup", "");
        Directory.SetLastWriteTimeUtc(interrupted, DateTime.UtcNow.AddDays(-2));
        report(BrowserProfileManager.TryDeleteAbandonedProfile(interrupted, root) && !Directory.Exists(interrupted),
            "Next stale-profile sweep finishes cleanup after the file is released", "");
        string linked = Create(true);
        string target = Path.Combine(root, "junction-target");
        Directory.CreateDirectory(target);
        string sentinel = Path.Combine(target, "keep.txt");
        File.WriteAllText(sentinel, "retain");
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path $env:STEALTH_TEST_LINK -Target $env:STEALTH_TEST_TARGET -ErrorAction Stop | Out-Null");
        start.Environment["STEALTH_TEST_LINK"] = Path.Combine(linked, "junction");
        start.Environment["STEALTH_TEST_TARGET"] = target;
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            process.WaitForExit();
            Directory.SetLastWriteTimeUtc(linked, DateTime.UtcNow.AddDays(-2));
            report(process.ExitCode == 0 && BrowserProfileManager.TryDeleteAbandonedProfile(linked, root) &&
                !Directory.Exists(linked) && File.ReadAllText(sentinel) == "retain",
                "Profile cleanup unlinks a directory junction while preserving its external target", "");
        }
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
