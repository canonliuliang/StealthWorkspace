using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace StealthWorkspace.Services;

internal static class VaultIntegritySelfTest
{
    private sealed class InlineProgress(Action<double> callback) : IProgress<double>
    {
        public void Report(double value) => callback(value);
    }

    internal static async Task RunAsync(string testRoot, Action<bool, string, string> report, Action<string, string>? skip = null)
    {
        string root = Path.Combine(testRoot, "vault-integrity");
        Directory.CreateDirectory(root);
        static void Require(bool value) { if (!value) throw new InvalidOperationException("Vault integrity assertion failed"); }
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); report(true, name, ""); }
            catch (Exception ex) { report(false, name, ex.ToString()); }
        }
        static async Task ExpectFailure(Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException) { return; }
            throw new InvalidOperationException("Expected operation to be rejected");
        }

        await Check("Vault: failed initial catalog write is retryable without restart", async () =>
        {
            string location = Path.Combine(root, "setup-retry");
            var vault = new VaultService(location);
            string blocked = Path.Combine(location, "system_catalog.dat.tmp");
            Directory.CreateDirectory(blocked);
            try
            {
                await ExpectFailure(() => vault.InitializeVaultAsync("RetryFixture#123"));
                Require(!vault.HasVault && !vault.IsUnlocked && !File.Exists(Path.Combine(location, "system_catalog.dat")));
            }
            finally { Directory.Delete(blocked, false); }
            await vault.InitializeVaultAsync("RetryFixture#123");
            Require(vault.HasVault && vault.IsUnlocked);
            vault.Lock();
            Require(vault.ApplyUnlock(vault.VerifyPassword("RetryFixture#123")));
            vault.Lock();
        });

        await Check("Vault: a lock during initial setup prevents automatic unlock", async () =>
        {
            var vault = new VaultService(Path.Combine(root, "setup-lock"));
            bool locked = false;
            var progress = new InlineProgress(value =>
            {
                if (value < 0.6 || locked) return;
                locked = true;
                vault.Lock();
            });
            await vault.InitializeVaultAsync("SetupLockFixture#123", progress: progress);
            Require(locked && vault.HasVault && !vault.IsUnlocked && vault.VisibleItems.Count == 0);
            Require(vault.ApplyUnlock(vault.VerifyPassword("SetupLockFixture#123")));
            vault.Lock();
        });

        await Check("Vault: locking a queued setup creates a locked vault", async () =>
        {
            var vault = new VaultService(Path.Combine(root, "queued-setup-lock"));
            var gate = (SemaphoreSlim)typeof(VaultService).GetField("_mutationGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vault)!;
            await gate.WaitAsync();
            Task setup;
            try
            {
                setup = vault.InitializeVaultAsync("QueuedSetup#123");
                Require(!setup.IsCompleted);
                vault.Lock();
            }
            finally { gate.Release(); }
            await setup;
            Require(vault.HasVault && !vault.IsUnlocked);
            Require(vault.ApplyUnlock(vault.VerifyPassword("QueuedSetup#123")));
            vault.Lock();
        });

        foreach (string gateName in new[] { "_sync", "_verificationGate" })
            await Check("Unlock: a lock revokes a request queued behind " + gateName, async () =>
            {
                var vault = new VaultService(Path.Combine(root, "queued-verify-" + gateName));
                await vault.InitializeVaultAsync("QueuedVerify#123");
                vault.Lock();
                object gate = typeof(VaultService).GetField(gateName, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vault)!;
                Task<VaultUnlockResult> request;
                // No sleeps: holding _sync prevents any worker from observing the
                // old epoch, while this caller can capture it through a reentrant lock.
                Monitor.Enter(gate);
                try
                {
                    request = vault.VerifyPasswordAsync("QueuedVerify#123");
                    Require(!request.IsCompleted);
                    vault.Lock();
                }
                finally { Monitor.Exit(gate); }
                using var result = await request;
                Require(result.Status == VaultUnlockStatus.Error && result.Key == null);
                Require(!vault.ApplyUnlock(result) && !vault.IsUnlocked);
                using var fresh = await vault.VerifyPasswordAsync("QueuedVerify#123");
                Require(vault.ApplyUnlock(fresh));
                vault.Lock();
            });

        var source = new VaultService(Path.Combine(root, "source"));
        await source.InitializeVaultAsync("IntegrityFixture#123", "IntegrityDecoy#123");
        string sample = Path.Combine(root, "source.bin");
        byte[] bytes = RandomNumberGenerator.GetBytes(96 * 1024 + 13);
        File.WriteAllBytes(sample, bytes);
        var item = await source.ImportFileAsync(sample, false);

        await Check("Vault: a restored catalog permits item deletion and decoy removal in the same session", async () =>
        {
            string location = Path.Combine(root, "backup-delete");
            var vault = new VaultService(location);
            await vault.InitializeVaultAsync("Recovery#123", "DecoyRecovery#123");
            await vault.ImportFileAsync(sample, false);
            vault.Lock();
            string catalog = Path.Combine(location, "system_catalog.dat");
            File.Copy(catalog, catalog + ".bak", true);
            File.WriteAllBytes(catalog, [1, 2, 3]);
            vault = new VaultService(location);
            Require(vault.ApplyUnlock(vault.VerifyPassword("Recovery#123")));
            await vault.DeleteItemAsync(vault.VisibleItems.Single());
            await vault.ClearDecoyAsync();
            Require(vault.VisibleItems.Count == 0 && !vault.HasDecoyVault);
        });

        await Check("Vault: temporarily unreadable primary is retried before validating the password", async () =>
        {
            string location = Path.Combine(root, "busy-primary");
            var vault = new VaultService(location);
            await vault.InitializeVaultAsync("OldRecovery#123");
            string primary = Path.Combine(location, "system_catalog.dat");
            byte[] older = File.ReadAllBytes(primary);
            await vault.ChangeMasterPasswordAsync("NewRecovery#123");
            vault.Lock();
            byte[] newer = File.ReadAllBytes(primary);
            File.WriteAllBytes(primary + ".bak", older);
            using (File.Open(primary, FileMode.Open, FileAccess.Read, FileShare.Delete))
            {
                vault = new VaultService(location);
                Require(vault.ApplyUnlock(vault.VerifyPassword("OldRecovery#123")));
                await ExpectFailure(() => vault.ImportFileAsync(sample, false));
                vault.Lock();
            }
            Require(vault.ApplyUnlock(vault.VerifyPassword("NewRecovery#123")));
            Require(File.ReadAllBytes(primary).AsSpan().SequenceEqual(newer));
        });

        foreach (string journalName in new[] { "rotate.dat", "remove.dat" })
        {
            await Check($"Export: unreadable {journalName} allows rescue and remains protected", async () =>
            {
                string journal = Path.Combine(root, "source", journalName);
                byte[] damaged = [1, 2, 3];
                File.WriteAllBytes(journal, damaged);
                try
                {
                    await ExpectFailure(() => source.ImportFileAsync(sample, false));
                    await ExpectFailure(() => source.ExportItemAsync(item, journal));
                    string target = Path.Combine(root, "rescue-" + journalName);
                    await source.ExportItemAsync(item, target);
                    Require(File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes));
                    Require(File.ReadAllBytes(journal).AsSpan().SequenceEqual(damaged));
                }
                finally { File.Delete(journal); }
            });
        }

        await Check("Vault: a missing deletion target is reconciled with a matching fallback catalog", async () =>
        {
            string location = Path.Combine(root, "fallback-removal");
            var vault = new VaultService(location);
            await vault.InitializeVaultAsync("Recovery#123");
            var doomed = await vault.ImportFileAsync(sample, false);
            await vault.ImportFileAsync(sample, false);
            string catalog = Path.Combine(location, "system_catalog.dat");
            File.Copy(catalog, catalog + ".bak", true);
            using (File.Open(catalog, FileMode.Open, FileAccess.Read, FileShare.Read))
                await ExpectFailure(() => vault.DeleteItemAsync(doomed));
            vault.Lock();
            File.WriteAllBytes(catalog, [1, 2, 3]);
            vault = new VaultService(location);
            Require(vault.ApplyUnlock(vault.VerifyPassword("Recovery#123")));
            Require(!File.Exists(Path.Combine(location, "remove.dat")));
            using var survivor = vault.GetDecryptedMemoryStream(vault.VisibleItems.Single());
            Require(survivor.ToArray().AsSpan().SequenceEqual(bytes));
        });

        await Check("Export: existing sibling .part file remains untouched", async () =>
        {
            string target = Path.Combine(root, "export.bin");
            byte[] sentinel = [17, 3, 9, 255];
            File.WriteAllBytes(target + ".part", sentinel);
            await source.ExportItemAsync(item, target);
            Require(File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes));
            Require(File.ReadAllBytes(target + ".part").AsSpan().SequenceEqual(sentinel));
        });

        await Check("Export: vault catalog and ciphertext cannot be overwritten", async () =>
        {
            foreach (string target in new[] { Path.Combine(root, "source", "system_catalog.dat"),
                Path.Combine(root, "source", "MediaVault", item.StoredFileName) })
            {
                byte[] before = File.ReadAllBytes(target);
                await ExpectFailure(() => source.ExportItemAsync(item, target));
                Require(File.ReadAllBytes(target).AsSpan().SequenceEqual(before));
            }
        });

        await Check("Export: cancellation after final decrypted chunk preserves destination", async () =>
        {
            string target = Path.Combine(root, "canceled-export.bin");
            byte[] sentinel = [4, 8, 15, 16, 23, 42];
            File.WriteAllBytes(target, sentinel);
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress(value => { if (value >= 1) cancellation.Cancel(); });
            await ExpectFailure(() => source.ExportItemAsync(item, target, progress, cancellation.Token));
            Require(cancellation.IsCancellationRequested && File.ReadAllBytes(target).AsSpan().SequenceEqual(sentinel));
            Require(!Directory.EnumerateFiles(root, "canceled-export.bin.*.part").Any());
        });

        await Check("Unlock: an explicit lock revokes an earlier successful result", () =>
        {
            using var result = source.VerifyPassword("IntegrityFixture#123");
            Require(result.Status == VaultUnlockStatus.Success);
            source.Lock();
            Require(!source.ApplyUnlock(result) && !source.IsUnlocked && result.Key == null);
            Require(source.ApplyUnlock(source.VerifyPassword("IntegrityFixture#123")));
            return Task.CompletedTask;
        });

        await Check("Unlock: results cannot be applied to another vault", async () =>
        {
            var other = new VaultService(Path.Combine(root, "other"));
            await other.InitializeVaultAsync("IntegrityFixture#123");
            other.Lock();
            using var result = source.VerifyPassword("IntegrityFixture#123");
            Require(!other.ApplyUnlock(result) && !other.IsUnlocked);
            Require(other.ApplyUnlock(other.VerifyPassword("IntegrityFixture#123")));
            other.Lock();
        });

        await Check("Unlock: result disposal clears the derived key and prevents reuse", () =>
        {
            var result = source.VerifyPassword("IntegrityFixture#123");
            byte[] key = result.Key ?? throw new InvalidOperationException("Missing verified key");
            result.Dispose();
            Require(key.All(value => value == 0) && result.Key == null && !source.ApplyUnlock(result));
            return Task.CompletedTask;
        });

        await Check("Unlock: a password rotation invalidates previously verified credentials", async () =>
        {
            using var old = source.VerifyPassword("IntegrityFixture#123");
            string journal = Path.Combine(root, "source", "rotate.dat");
            bool checkedCommit = false;
            var progress = new InlineProgress(value =>
            {
                if (value != 1 || checkedCommit) return;
                using var clear = source.GetDecryptedMemoryStream(item);
                Require(clear.ToArray().AsSpan().SequenceEqual(bytes));
                checkedCommit = true;
            });
            using (var pinned = source.OpenDecryptedStream(item))
                await source.ChangeMasterPasswordAsync("RotatedIntegrity#123", progress);
            Require(checkedCommit && File.Exists(journal));
            Require(!source.ApplyUnlock(old));
            source.Lock();
            Require(source.ApplyUnlock(source.VerifyPassword("RotatedIntegrity#123")));
            Require(!File.Exists(journal));
            using var stream = source.OpenDecryptedStream(source.VisibleItems.Single());
            Require(SHA256.HashData(stream).AsSpan().SequenceEqual(SHA256.HashData(bytes)));
        });

        await Check("Unlock: a successful result is applied only once", () =>
        {
            source.Lock();
            using var result = source.VerifyPassword("RotatedIntegrity#123");
            Require(source.ApplyUnlock(result) && !source.ApplyUnlock(result) && source.IsUnlocked);
            return Task.CompletedTask;
        });
        source.Lock();

        await Check("Rotation: a lock or switch to decoy during the change preserves the new session", async () =>
        {
            var vault = new VaultService(Path.Combine(root, "rotation-session"));
            await vault.InitializeVaultAsync("InitialMain#123", "UnchangedDecoy#123");
            vault.Lock();
            Require(vault.ApplyUnlock(vault.VerifyPassword("UnchangedDecoy#123")));
            await vault.ImportFileAsync(sample, false);
            foreach (bool switchToDecoy in new[] { false, true })
            {
                string old = switchToDecoy ? "AfterLock#123" : "InitialMain#123";
                string next = switchToDecoy ? "AfterSwitch#123" : "AfterLock#123";
                vault.Lock();
                Require(vault.ApplyUnlock(vault.VerifyPassword(old)));
                await vault.ChangeMasterPasswordAsync(next, new InlineProgress(value =>
                {
                    if (value != 0.02) return;
                    vault.Lock();
                    if (switchToDecoy) Require(vault.ApplyUnlock(vault.VerifyPassword("UnchangedDecoy#123")));
                }));
                Require(vault.IsUnlocked == switchToDecoy && vault.IsDecoyMode == switchToDecoy);
                if (switchToDecoy)
                {
                    using var clear = vault.GetDecryptedMemoryStream(vault.VisibleItems.Single());
                    Require(clear.ToArray().AsSpan().SequenceEqual(bytes));
                }
            }
        });

        await Check("Import: verified original removal supports ordinary and read-only files", async () =>
        {
            var vault = new VaultService(Path.Combine(root, "verified-removal"));
            await vault.InitializeVaultAsync("VerifiedRemoval#123");
            foreach (bool readOnly in new[] { false, true })
            {
                string original = Path.Combine(root, readOnly ? "verified-readonly.bin" : "verified-ordinary.bin");
                File.WriteAllBytes(original, bytes);
                if (readOnly) File.SetAttributes(original, FileAttributes.ReadOnly);
                bool removed = false;
                var imported = await vault.ImportFileAsync(original, true, reportOriginalRemoval: value => removed = value);
                Require(removed && !File.Exists(original));
                using var stream = vault.OpenDecryptedStream(imported);
                Require(SHA256.HashData(stream).AsSpan().SequenceEqual(SHA256.HashData(bytes)));
            }
            vault.Lock();
        });

        foreach (bool replaceIdentity in new[] { true, false })
            await Check("Import: preserves changed source " + (replaceIdentity ? "identity" : "bytes with restored timestamp"), async () =>
            {
                string location = Path.Combine(root, replaceIdentity ? "replaced-source-vault" : "edited-source-vault");
                var vault = new VaultService(location);
                await vault.InitializeVaultAsync("SourceIdentity#123");
                string original = Path.Combine(root, replaceIdentity ? "replaced.bin" : "edited.bin");
                File.WriteAllBytes(original, bytes);
                DateTime writeTime = File.GetLastWriteTimeUtc(original);
                byte[] replacement = (byte[])bytes.Clone(); replacement[100] ^= 0x40;
                bool edited = false;
                vault.VisibleItems.CollectionChanged += (_, _) =>
                {
                    if (edited || vault.VisibleItems.Count == 0) return;
                    edited = true;
                    if (replaceIdentity) File.Move(original, original + ".original");
                    File.WriteAllBytes(original, replacement);
                    File.SetLastWriteTimeUtc(original, writeTime);
                };
                bool removed = true;
                var imported = await vault.ImportFileAsync(original, true, reportOriginalRemoval: value => removed = value);
                Require(edited && !removed && File.ReadAllBytes(original).AsSpan().SequenceEqual(replacement));
                using var stream = vault.OpenDecryptedStream(imported);
                Require(SHA256.HashData(stream).AsSpan().SequenceEqual(SHA256.HashData(bytes)));
                vault.Lock();
            });

        await Check("Import: lock and same-space re-unlock cancel the old operation", async () =>
        {
            var vault = new VaultService(Path.Combine(root, "epoch-import"));
            await vault.InitializeVaultAsync("ImportEpoch#123");
            bool relocked = false;
            var progress = new InlineProgress(value =>
            {
                if (value <= 0 || relocked) return;
                relocked = true;
                vault.Lock();
                Require(vault.ApplyUnlock(vault.VerifyPassword("ImportEpoch#123")));
            });
            await ExpectFailure(() => vault.ImportFileAsync(sample, false, progress));
            Require(relocked && vault.IsUnlocked && vault.VisibleItems.Count == 0);
            Require(!Directory.EnumerateFiles(Path.Combine(root, "epoch-import", "MediaVault")).Any());
            Require(File.ReadAllBytes(sample).AsSpan().SequenceEqual(bytes));
            vault.Lock();
        });

        await Check("Library: queued imports, export, rename and deletion cannot cross a re-unlock", async () =>
        {
            var vault = new VaultService(Path.Combine(root, "queued-library-session"));
            await vault.InitializeVaultAsync("LibraryEpoch#123");
            var item = await vault.ImportFileAsync(sample, false);
            var gate = (SemaphoreSlim)typeof(VaultService).GetField("_mutationGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vault)!;
            string export = Path.Combine(root, "revoked-export.bin");
            using var memory = new MemoryStream(bytes, writable: false);
            await gate.WaitAsync();
            Task[] requests;
            try
            {
                requests = [
                    vault.ImportFileAsync(sample, true),
                    vault.ImportStreamAsync(memory, "queued.png", new InlineProgress(_ => { }), default),
                    vault.ExportItemAsync(item, export),
                    vault.RenameItemAsync(item, "new-name" + item.FileExtension),
                    vault.DeleteItemsAsync([item])
                ];
                Require(requests.All(request => !request.IsCompleted));
                vault.Lock();
                Require(vault.ApplyUnlock(vault.VerifyPassword("LibraryEpoch#123")));
            }
            finally { gate.Release(); }
            foreach (var request in requests) await ExpectFailure(() => request);
            Require(vault.VisibleItems.Single().OriginalFileName == Path.GetFileName(sample));
            Require(!File.Exists(export) && File.ReadAllBytes(sample).AsSpan().SequenceEqual(bytes));
            vault.Lock();
        });

        await Check("Vault: a nonempty payload truncated to an empty header is rejected", async () =>
        {
            string location = Path.Combine(root, "truncated-header");
            var vault = new VaultService(location);
            await vault.InitializeVaultAsync("TruncatedFixture#123");
            var imported = await vault.ImportFileAsync(sample, false);
            string stored = Path.Combine(location, "MediaVault", imported.StoredFileName);
            byte[] original = File.ReadAllBytes(stored);
            byte[] header = original[..CryptoService.HeaderSizeV2];
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16, 8), 0);
            File.WriteAllBytes(stored, header);
            bool streamRejected = false, memoryRejected = false;
            try { using var stream = vault.OpenDecryptedStream(imported); } catch (InvalidDataException) { streamRejected = true; }
            try { using var stream = vault.GetDecryptedMemoryStream(imported); } catch (InvalidDataException) { memoryRejected = true; }
            await ExpectFailure(() => vault.ExportItemAsync(imported, Path.Combine(root, "must-not-export.bin")));
            await ExpectFailure(() => vault.ChangeMasterPasswordAsync("MustNotRotate#123"));
            Require(streamRejected && memoryRejected && File.ReadAllBytes(stored).AsSpan().SequenceEqual(header));
            Require(!File.Exists(Path.Combine(root, "must-not-export.bin")));
            File.WriteAllBytes(stored, original);
            vault.Lock();
            Require(vault.ApplyUnlock(vault.VerifyPassword("TruncatedFixture#123")));
            using var restored = vault.OpenDecryptedStream(vault.VisibleItems.Single());
            Require(SHA256.HashData(restored).AsSpan().SequenceEqual(SHA256.HashData(bytes)));
            vault.Lock();
        });

        string boundaryRoot = Path.Combine(root, "vault boundary with long folder name");
        var boundaryVault = new VaultService(boundaryRoot);
        await boundaryVault.InitializeVaultAsync("BoundaryFixture#123");
        var boundaryItem = await boundaryVault.ImportFileAsync(sample, false);
        async Task CheckAlias(string alias)
        {
            string realCatalog = Path.Combine(boundaryRoot, "system_catalog.dat");
            string realMedia = Path.Combine(boundaryRoot, "MediaVault", boundaryItem.StoredFileName);
            byte[] catalog = File.ReadAllBytes(realCatalog), media = File.ReadAllBytes(realMedia);
            Require(SafeFileSystem.ResolvePath(boundaryRoot) == SafeFileSystem.ResolvePath(alias));
            await ExpectFailure(() => boundaryVault.ImportFileAsync(Path.Combine(alias, "MediaVault", boundaryItem.StoredFileName), true));
            await ExpectFailure(() => boundaryVault.ExportItemAsync(boundaryItem, Path.Combine(alias, "system_catalog.dat")));
            await ExpectFailure(() => boundaryVault.ExportItemAsync(boundaryItem, Path.Combine(alias, "new folder", "plain.bin")));
            await ExpectFailure(() => boundaryVault.CreateBackupAsync(Path.Combine(alias, "backup.swbackup")));
            Require(File.ReadAllBytes(realCatalog).AsSpan().SequenceEqual(catalog));
            Require(File.ReadAllBytes(realMedia).AsSpan().SequenceEqual(media));
            Require(boundaryVault.VisibleItems.Count == 1 && !Directory.Exists(Path.Combine(boundaryRoot, "new folder")));
        }

        await Check("Vault boundary: ordinary path and new descendant are protected", () => CheckAlias(boundaryRoot));
        var shortPath = new StringBuilder(32768);
        uint shortLength = GetShortPathNameW(boundaryRoot, shortPath, (uint)shortPath.Capacity);
        if (shortLength > 0 && shortLength < shortPath.Capacity &&
            !string.Equals(shortPath.ToString(), boundaryRoot, StringComparison.OrdinalIgnoreCase))
            await Check("Vault boundary: real 8.3 alias cannot bypass import/export/backup protection", () => CheckAlias(shortPath.ToString()));
        else skip?.Invoke("Vault boundary: real 8.3 alias", "The fixture volume did not provide a distinct short name.");

        // Inspect existing DOS mappings only. Never create or alter a user drive mapping.
        string? substitute = null;
        for (char drive = 'A'; drive <= 'Z' && substitute == null; drive++)
        {
            var mapped = new StringBuilder(32768);
            if (QueryDosDeviceW(drive + ":", mapped, mapped.Capacity) == 0) continue;
            string definition = mapped.ToString();
            if (!definition.StartsWith(@"\??\", StringComparison.Ordinal)) continue;
            string target = definition[4..].TrimEnd(Path.DirectorySeparatorChar);
            if (boundaryRoot.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                substitute = drive + ":" + boundaryRoot[target.Length..];
        }
        if (substitute != null)
            await Check("Vault boundary: existing SUBST alias cannot bypass protection", () => CheckAlias(substitute));
        else skip?.Invoke("Vault boundary: SUBST alias", "No existing DOS drive mapping covers the synthetic fixture; no drive mapping was created.");
        boundaryVault.Lock();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetShortPathNameW(string path, StringBuilder shortPath, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint QueryDosDeviceW(string name, StringBuilder target, int length);
}
