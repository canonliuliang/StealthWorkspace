using System.Security.Cryptography;

namespace StealthWorkspace.Services;

internal static class LegacyVaultMigrationSelfTest
{
    private sealed record Fixture(string Legacy, Dictionary<string, byte[]> Original, string[] MediaNames);

    internal static async Task RunAsync(string testRoot, Action<bool, string, string> report)
    {
        string root = Path.Combine(testRoot, "legacy-vault-migration");
        Directory.CreateDirectory(root);
        byte[] bytes = RandomNumberGenerator.GetBytes(128 * 1024 + 17);
        string sample = Path.Combine(root, "sample.bin");
        File.WriteAllBytes(sample, bytes);
        static void Require(bool value) { if (!value) throw new InvalidOperationException("Legacy migration assertion failed"); }
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); report(true, "Legacy vault: " + name, ""); }
            catch (Exception ex) { report(false, "Legacy vault: " + name, ex.ToString()); }
        }
        async Task<Fixture> Create(string name)
        {
            string originalRoot = Path.Combine(root, name + "-fixture");
            var original = new VaultService(originalRoot);
            await original.InitializeVaultAsync("LegacyFixture#123");
            var first = await original.ImportFileAsync(sample, false);
            var second = await original.ImportFileAsync(sample, false);
            original.Lock();
            string legacy = Path.Combine(root, name + "-legacy");
            Directory.CreateDirectory(Path.Combine(legacy, "EncryptedMedia"));
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(originalRoot, "system_catalog.dat")), null, DataProtectionScope.CurrentUser);
            try { File.WriteAllBytes(Path.Combine(legacy, "vault_config.dat"), plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            foreach (string file in new[] { first.StoredFileName, second.StoredFileName })
                File.Copy(Path.Combine(originalRoot, "MediaVault", file), Path.Combine(legacy, "EncryptedMedia", file));
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "Synthetic legacy sidecar that the vault must preserve");
            return new(legacy, Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(legacy, path), File.ReadAllBytes), [first.StoredFileName, second.StoredFileName]);
        }
        static void SourceIntact(Fixture fixture)
        {
            foreach (var file in fixture.Original)
                Require(File.ReadAllBytes(Path.Combine(fixture.Legacy, file.Key)).AsSpan().SequenceEqual(file.Value));
        }
        void Verify(VaultService vault)
        {
            Require(vault.ApplyUnlock(vault.VerifyPassword("LegacyFixture#123")) && vault.VisibleItems.Count == 2);
            foreach (var item in vault.VisibleItems)
            {
                using var decrypted = vault.OpenDecryptedStream(item);
                Require(SHA256.HashData(decrypted).AsSpan().SequenceEqual(SHA256.HashData(bytes)));
            }
            vault.Lock();
        }

        await Check("successful migration preserves every legacy source and reopens", async () =>
        {
            Fixture fixture = await Create("success");
            string destination = Path.Combine(root, "success-current");
            Verify(new VaultService(destination, fixture.Legacy));
            SourceIntact(fixture);
            Verify(new VaultService(destination, fixture.Legacy));
            SourceIntact(fixture);
            Require(!Directory.EnumerateDirectories(destination, ".legacy-migration-*").Any());
        });

        await Check("partial copy failure leaves no committed target and retries", async () =>
        {
            Fixture fixture = await Create("busy");
            string destination = Path.Combine(root, "busy-current");
            string readOnlySource = Path.Combine(fixture.Legacy, "EncryptedMedia", fixture.MediaNames[0]);
            FileAttributes originalAttributes = File.GetAttributes(readOnlySource);
            File.SetAttributes(readOnlySource, originalAttributes | FileAttributes.ReadOnly);
            try
            {
                VaultService incomplete;
                using (var held = new FileStream(Path.Combine(fixture.Legacy, "EncryptedMedia", fixture.MediaNames[1]), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    incomplete = new VaultService(destination, fixture.Legacy);
                    Require(incomplete.IsCorrupted && incomplete.HasVault && !File.Exists(Path.Combine(destination, "system_catalog.dat")));
                    Require(!Directory.EnumerateFileSystemEntries(Path.Combine(destination, "MediaVault")).Any());
                    Require(!Directory.EnumerateDirectories(destination, ".legacy-migration-*").Any());
                }
                SourceIntact(fixture);
                incomplete.ReloadConfig();
                Verify(incomplete);
                Verify(new VaultService(destination, fixture.Legacy));
                SourceIntact(fixture);
                Require((File.GetAttributes(readOnlySource) & FileAttributes.ReadOnly) != 0);
                Require((File.GetAttributes(Path.Combine(destination, "MediaVault", fixture.MediaNames[0])) & FileAttributes.ReadOnly) == 0);
            }
            finally { File.SetAttributes(readOnlySource, originalAttributes); }
        });

        await Check("existing target never receives unrelated legacy media", async () =>
        {
            Fixture fixture = await Create("existing");
            string destination = Path.Combine(root, "existing-current");
            var existing = new VaultService(destination);
            await existing.InitializeVaultAsync("CurrentFixture#123");
            var item = await existing.ImportFileAsync(sample, false);
            existing.Lock();
            byte[] catalog = File.ReadAllBytes(Path.Combine(destination, "system_catalog.dat"));
            byte[] media = File.ReadAllBytes(Path.Combine(destination, "MediaVault", item.StoredFileName));
            var reopened = new VaultService(destination, fixture.Legacy);
            Require(reopened.ApplyUnlock(reopened.VerifyPassword("CurrentFixture#123")) && reopened.VisibleItems.Count == 1);
            Require(File.ReadAllBytes(Path.Combine(destination, "system_catalog.dat")).AsSpan().SequenceEqual(catalog));
            Require(File.ReadAllBytes(Path.Combine(destination, "MediaVault", item.StoredFileName)).AsSpan().SequenceEqual(media));
            Require(Directory.EnumerateFiles(Path.Combine(destination, "MediaVault")).Count() == 1);
            SourceIntact(fixture);
            reopened.Lock();
        });

        await Check("unindexed target media blocks migration without overwriting", async () =>
        {
            Fixture fixture = await Create("conflict");
            string destination = Path.Combine(root, "conflict-current");
            string mediaRoot = Path.Combine(destination, "MediaVault");
            Directory.CreateDirectory(mediaRoot);
            string sentinel = Path.Combine(mediaRoot, fixture.MediaNames[0]);
            File.WriteAllBytes(sentinel, bytes);
            var incomplete = new VaultService(destination, fixture.Legacy);
            Require(incomplete.IsCorrupted && incomplete.HasVault && !File.Exists(Path.Combine(destination, "system_catalog.dat")));
            Require(File.ReadAllBytes(sentinel).AsSpan().SequenceEqual(bytes) && Directory.EnumerateFiles(mediaRoot).Count() == 1);
            SourceIntact(fixture);
        });

        await Check("malformed legacy catalog is retained and cannot advertise an empty vault", async () =>
        {
            Fixture fixture = await Create("malformed");
            string catalog = Path.Combine(fixture.Legacy, "vault_config.dat");
            File.WriteAllText(catalog, "{ incomplete but potentially recoverable");
            fixture.Original["vault_config.dat"] = File.ReadAllBytes(catalog);
            string destination = Path.Combine(root, "malformed-current");
            var incomplete = new VaultService(destination, fixture.Legacy);
            Require(incomplete.IsCorrupted && incomplete.HasVault && !File.Exists(Path.Combine(destination, "system_catalog.dat")));
            Require(!Directory.EnumerateFileSystemEntries(Path.Combine(destination, "MediaVault")).Any());
            SourceIntact(fixture);
        });

        await Check("reset never resurrects the preserved legacy source after reload or relocation", async () =>
        {
            Fixture fixture = await Create("reset-retained");
            string destination = Path.Combine(root, "reset-retained-current");
            var vault = new VaultService(destination, fixture.Legacy);
            Verify(vault);
            await vault.ResetVaultAsync();
            Require(!vault.HasVault && !vault.IsCorrupted && File.Exists(Path.Combine(destination, AppPaths.LegacyImportDisabledFileName)));
            vault.ReloadConfig();
            Require(!vault.HasVault && !vault.IsCorrupted);
            var reopened = new VaultService(destination, fixture.Legacy);
            Require(!reopened.HasVault && !reopened.IsCorrupted && reopened.VisibleItems.Count == 0);
            Require(!Directory.EnumerateFiles(Path.Combine(destination, "MediaVault")).Any());
            Require(AppPaths.ResolveDataRoot(Path.Combine(root, "reset-retained-fixture"), destination,
                Path.Combine(root, "reset-unused-portable")) == destination);
            string relocated = Path.Combine(root, "reset-relocated");
            Require(AppPaths.TryCopyLegacyVault(destination, relocated));
            Require(File.Exists(Path.Combine(relocated, AppPaths.LegacyImportDisabledFileName)));
            var moved = new VaultService(relocated, fixture.Legacy);
            Require(!moved.HasVault && !moved.IsCorrupted);
            SourceIntact(fixture);
        });

        await Check("reset marker write failure preserves every payload and retains recovery intent", async () =>
        {
            Fixture fixture = await Create("blocked-reset-marker");
            string destination = Path.Combine(root, "blocked-reset-marker-current");
            var vault = new VaultService(destination, fixture.Legacy);
            Verify(vault);
            string catalogPath = Path.Combine(destination, "system_catalog.dat");
            byte[] catalog = File.ReadAllBytes(catalogPath);
            var media = fixture.MediaNames.ToDictionary(name => name,
                name => File.ReadAllBytes(Path.Combine(destination, "MediaVault", name)));
            string marker = Path.Combine(destination, AppPaths.LegacyImportDisabledFileName);
            Directory.CreateDirectory(marker);
            try
            {
                bool failed = false;
                try { await vault.ResetVaultAsync(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
                Require(failed && vault.HasVault && vault.IsCorrupted && File.Exists(Path.Combine(destination, "remove.dat")));
                Require(File.ReadAllBytes(catalogPath).AsSpan().SequenceEqual(catalog));
                foreach (var item in media)
                    Require(File.ReadAllBytes(Path.Combine(destination, "MediaVault", item.Key)).AsSpan().SequenceEqual(item.Value));
                vault = new VaultService(destination, fixture.Legacy);
                Require(vault.HasVault && vault.IsCorrupted);
                SourceIntact(fixture);
            }
            finally { Directory.Delete(marker, false); }
            await vault.ResetVaultAsync();
            Require(!vault.HasVault && !vault.IsCorrupted && File.Exists(marker));
            Require(!new VaultService(destination, fixture.Legacy).HasVault);
            SourceIntact(fixture);
        });
    }
}
