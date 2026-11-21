namespace StealthWorkspace.Services;

internal static class BrowserProfileSafetyTests
{
    internal static async Task CheckAccountsAsync(string testRoot, Action<bool,string,string> report)
    {
        string root = Path.Combine(testRoot,"account-vault");
        string other = Path.Combine(testRoot,"other-account-vault");
        var vault = new VaultService(root);
        await vault.InitializeVaultAsync("AccountTest#123");
        string identity = vault.GetWebsiteAccountIdentity();
        string account = BrowserAccountDirectory.ForVault(root,identity);
        string foreign = BrowserAccountDirectory.ForVault(other,identity);
        report(account != foreign && account == BrowserAccountDirectory.ForVault(Path.Combine(root,"."),identity) &&
            !Path.GetFileName(account).StartsWith("Stealth_WebProfile_",StringComparison.Ordinal),
            "Personal accounts have a stable path per vault separate from disposable profile sweeps", "");
        using (BrowserAccountDirectory.Acquire(root,account,identity))
        {
            File.WriteAllText(Path.Combine(account,"fixture"),"synthetic session");
            bool protectedLease = false;
            try { using var ignored = BrowserAccountDirectory.Acquire(root,account,identity); }
            catch (IOException) { protectedLease = true; }
            report(protectedLease && !BrowserProfileManager.TryDeleteAbandonedProfile(account,Path.GetDirectoryName(account)!),
                "Persistent profile is exclusively leased and rejected by the stale volatile-profile sweep", "");
        }
        using (BrowserAccountDirectory.Acquire(other,foreign,identity)) File.WriteAllText(Path.Combine(foreign,"fixture"),"keep");
        // Simulate an external portable-folder restore, which bypasses the
        // application's normal reset/restore hook. Use only synthetic catalogs.
        var replacement = new VaultService(Path.Combine(testRoot,"manual-account-source"));
        await replacement.InitializeVaultAsync("DifferentVault#123");
        replacement.Lock();
        string catalog = Path.Combine(root,"system_catalog.dat");
        byte[] originalCatalog = File.ReadAllBytes(catalog);
        vault.Lock();
        File.Copy(Path.Combine(testRoot,"manual-account-source","system_catalog.dat"),catalog,true);
        var swapped = new VaultService(root);
        if (!swapped.ApplyUnlock(swapped.VerifyPassword("DifferentVault#123"))) throw new InvalidOperationException("Synthetic replacement unlock failed");
        string changedIdentity = swapped.GetWebsiteAccountIdentity();
        string changedPath = BrowserAccountDirectory.ForVault(root,changedIdentity);
        string peerFixture = Path.Combine(changedPath,"other-catalog-fixture");
        using (BrowserAccountDirectory.Acquire(root,changedPath,changedIdentity))
        {
            report(changedPath != account && !File.Exists(Path.Combine(changedPath,"fixture")) && File.Exists(Path.Combine(account,"fixture")),
                "Manually replacing a catalog at the same path opens an empty website profile and preserves the original vault's logins", "");
            File.WriteAllText(peerFixture,"peer");
        }
        swapped.Lock();
        File.WriteAllBytes(catalog,originalCatalog);
        vault = new VaultService(root);
        if (!vault.ApplyUnlock(vault.VerifyPassword("AccountTest#123"))) throw new InvalidOperationException("Could not reopen original synthetic vault");
        report(vault.GetWebsiteAccountIdentity()==identity && File.Exists(Path.Combine(account,"fixture")),
            "Switching the original catalog back at the same location reattaches only its own profile", "");
        string backup = Path.Combine(testRoot,"test-account-vault.swbackup");
        await vault.CreateBackupAsync(backup);
        await vault.ResetVaultAsync();
        report(!Directory.Exists(account) && File.ReadAllText(Path.Combine(foreign,"fixture"))=="keep" && File.ReadAllText(peerFixture)=="peer",
            "Confirmed reset removes only its catalog's accounts and preserves other catalogs at both the same and different locations", "");
        using (BrowserAccountDirectory.Acquire(root,account,identity)) File.WriteAllText(Path.Combine(account,"old-login-fixture"),"stale");
        await vault.RestoreBackupAsync(backup);
        report(!Directory.Exists(account) && vault.HasVault && File.ReadAllText(peerFixture)=="peer",
            "Restoring a vault removes its old login and retains a different catalog's website data at the same location", "");
        File.WriteAllBytes(catalog, new byte[] { 1, 2, 3 });
        var unreadable = new VaultService(root);
        await unreadable.ResetVaultAsync();
        await unreadable.InitializeVaultAsync("NewAfterFailure#123");
        string freshIdentity = unreadable.GetWebsiteAccountIdentity();
        report(freshIdentity != changedIdentity && freshIdentity != identity && File.ReadAllText(peerFixture) == "peer" &&
            !Directory.Exists(BrowserAccountDirectory.ForVault(root,freshIdentity)),
            "Reinitializing after an unreadable catalog neither adopts nor erases unidentifiable website accounts", "");
        unreadable.Lock();
        string safe = Path.Combine(testRoot,"unrelated-account-vault");
        string unknown = BrowserAccountDirectory.GroupForVault(safe);
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown,"unrelated"),"preserve");
        bool rejected = false;
        try { using var ignored = BrowserAccountDirectory.Acquire(safe,BrowserAccountDirectory.ForVault(safe,identity),identity); }
        catch (IOException) { rejected = true; }
        report(rejected && File.ReadAllText(Path.Combine(unknown,"unrelated"))=="preserve","Unknown nonempty account directories are never adopted", "");
        File.Delete(Path.Combine(unknown,"unrelated")); // Only this fixture, never a recursive delete.
        Directory.Delete(unknown);
        await BrowserAccountDirectory.RemoveForReplacementAsync(root,changedIdentity);
        await BrowserAccountDirectory.RemoveForReplacementAsync(other,identity);
        await CheckLegacyIdentityAsync(testRoot,report);
    }

    private static async Task CheckLegacyIdentityAsync(string testRoot, Action<bool,string,string> report)
    {
        string root = Path.Combine(testRoot,"legacy-account-id");
        var vault = new VaultService(root);
        await vault.InitializeVaultAsync("LegacyAccount#123","LegacyDecoy#123");
        vault.Lock();
        string catalog = Path.Combine(root,"system_catalog.dat");
        byte[] plaintext = System.Security.Cryptography.ProtectedData.Unprotect(File.ReadAllBytes(catalog),null,System.Security.Cryptography.DataProtectionScope.CurrentUser);
        var json = System.Text.Json.Nodes.JsonNode.Parse(plaintext)!.AsObject();
        json.Remove(nameof(VaultConfig.WebsiteAccountIdentity));
        byte[] legacy = System.Security.Cryptography.ProtectedData.Protect(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(json),null,System.Security.Cryptography.DataProtectionScope.CurrentUser);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        File.WriteAllBytes(catalog,legacy);
        vault = new VaultService(root);
        bool guarded = false;
        try { _ = vault.GetWebsiteAccountIdentity(); } catch (InvalidOperationException) { guarded = true; }
        if (!vault.ApplyUnlock(vault.VerifyPassword("LegacyDecoy#123"))) throw new InvalidOperationException("Synthetic decoy unlock failed");
        bool guardedDecoy = false;
        try { _ = vault.GetWebsiteAccountIdentity(); } catch (InvalidOperationException) { guardedDecoy = true; }
        vault.Lock();
        if (!vault.ApplyUnlock(vault.VerifyPassword("LegacyAccount#123"))) throw new InvalidOperationException("Synthetic legacy unlock failed");
        string identity = vault.GetWebsiteAccountIdentity();
        bool readOnly = legacy.SequenceEqual(File.ReadAllBytes(catalog));
        await vault.ChangeMasterPasswordAsync("ChangedAccount#456");
        vault.Lock();
        vault = new VaultService(root);
        bool rotated = vault.ApplyUnlock(vault.VerifyPassword("ChangedAccount#456"));
        report(guarded && guardedDecoy && rotated && readOnly && identity == vault.GetWebsiteAccountIdentity(),
            "Legacy account identity needs no browsing-time catalog write, rejects locked/decoy access and survives master-key rotation and restart", "");
        vault.Lock();
    }

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
