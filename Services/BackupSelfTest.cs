using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StealthWorkspace.Services;

internal static class BackupSelfTest
{
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    internal static async Task RunAsync(string root, Action<bool, string, string> report)
    {
        void Check(bool condition, string name) => report(condition, name, "");
        string dataRoot = Path.Combine(root, "backup-source");
        var source = new VaultService(dataRoot);
        await source.InitializeVaultAsync("Backup#123", "DecoyBackup#123");
        string sample = Path.Combine(root, "backup-sample.bin");
        byte[] data = RandomNumberGenerator.GetBytes(1_100_007);
        await File.WriteAllBytesAsync(sample, data);
        var original = await source.ImportFileAsync(sample, false);
        source.Lock();
        source.ApplyUnlock(source.VerifyPassword("DecoyBackup#123"));
        await source.ImportFileAsync(sample, false);
        source.Lock();
        source.ApplyUnlock(source.VerifyPassword("Backup#123"));

        string backup = Path.Combine(root, "roundtrip.swbackup");
        await source.CreateBackupAsync(backup);
        Check(await source.VerifyBackupAsync(backup) == 2, "Backup includes primary and decoy encrypted media");
        byte[] before = File.ReadAllBytes(backup);
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            bool rejected = false;
            try { await source.CreateBackupAsync(backup, cancellationToken: cancel.Token); }
            catch (OperationCanceledException) { rejected = true; }
            Check(rejected && File.ReadAllBytes(backup).SequenceEqual(before), "Canceled backup preserves previous backup");
            int existing = source.VisibleItems.Count;
            rejected = false;
            try { await source.ImportFileAsync(sample, false, cancellationToken: cancel.Token); }
            catch (OperationCanceledException) { rejected = true; }
            Check(rejected && source.VisibleItems.Count == existing && !Directory.EnumerateFiles(Path.Combine(dataRoot, "MediaVault"), "*.part").Any(),
                "Canceled import leaves no index entry or partial ciphertext");
        }

        string restoredRoot = Path.Combine(root, "backup-restored");
        var restored = new VaultService(restoredRoot);
        Check(await restored.RestoreBackupAsync(backup) == 2, "Restore backup into empty vault");
        Check(restored.VerifyPassword("wrong").Status == VaultUnlockStatus.WrongPassword, "Restored vault rejects wrong password");
        Check(restored.ApplyUnlock(restored.VerifyPassword("Backup#123")), "Restored vault accepts original password");
        using (Stream stream = restored.OpenDecryptedStream(restored.VisibleItems.Single()))
            Check(SHA256.HashData(stream).SequenceEqual(SHA256.HashData(data)), "Restored primary media decrypted hash");
        restored.Lock();
        Check(restored.ApplyUnlock(restored.VerifyPassword("DecoyBackup#123")), "Restored decoy accepts original password");
        using (Stream stream = restored.OpenDecryptedStream(restored.VisibleItems.Single()))
            Check(SHA256.HashData(stream).SequenceEqual(SHA256.HashData(data)), "Restored decoy media decrypted hash");
        restored.Lock();
        bool overwriteRejected = false;
        byte[] catalogBefore = File.ReadAllBytes(Path.Combine(restoredRoot, "system_catalog.dat"));
        try { await restored.RestoreBackupAsync(backup); }
        catch (InvalidOperationException) { overwriteRejected = true; }
        Check(overwriteRejected && File.ReadAllBytes(Path.Combine(restoredRoot, "system_catalog.dat")).SequenceEqual(catalogBefore),
            "Restore refuses to overwrite existing vault");

        var reopened = new VaultService(restoredRoot);
        Check(reopened.ApplyUnlock(reopened.VerifyPassword("Backup#123")) && reopened.VisibleItems.Count == 1, "Restored vault reopens from disk");
        reopened.Lock();
        string damaged = Path.Combine(root, "damaged.swbackup");
        File.Copy(backup, damaged);
        using (var archive = ZipFile.Open(damaged, ZipArchiveMode.Update))
        {
            var entry = archive.Entries.First(e => e.FullName.EndsWith(".svault"));
            using var stream = entry.Open();
            int value = stream.ReadByte();
            stream.Position = 0;
            stream.WriteByte((byte)(value ^ 0x40));
        }
        var empty = new VaultService(Path.Combine(root, "damaged-target"));
        bool damagedRejected = false;
        try { await empty.RestoreBackupAsync(damaged); }
        catch (InvalidDataException) { damagedRejected = true; }
        Check(damagedRejected && !empty.HasVault && !Directory.EnumerateFiles(Path.Combine(root, "damaged-target", "MediaVault")).Any(),
            "Damaged backup rejected without committing files");

        string traversal = Path.Combine(root, "invalid-path.swbackup");
        using (var archive = ZipFile.Open(traversal, ZipArchiveMode.Create))
        {
            using var manifest = archive.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(manifest, new { Version = 1, CreatedUtc = DateTime.UtcNow,
                Files = new[] { new { Name = "MediaVault/../../escape.svault", Length = 0, Sha256 = new string('0', 64) } } });
        }
        bool pathRejected = false;
        try { await empty.RestoreBackupAsync(traversal); }
        catch (InvalidDataException) { pathRejected = true; }
        Check(pathRejected && !File.Exists(Path.Combine(root, "escape.svault")), "Backup path traversal rejected");
        Check(File.ReadAllBytes(sample).SequenceEqual(data), "Backup and restore preserve source media");

        async Task<bool> Rejected(Func<Task> operation)
        {
            try { await operation(); return false; }
            catch (Exception ex) when (ex is InvalidDataException or IOException or OperationCanceledException or InvalidOperationException) { return true; }
        }

        using (var cancelReadback = new CancellationTokenSource())
        {
            byte[] previous = File.ReadAllBytes(backup);
            var progress = new InlineProgress(value =>
            {
                if (value <= 0.5 || cancelReadback.IsCancellationRequested) return;
                var pending = Directory.EnumerateFiles(root, "roundtrip.swbackup.*.part").SingleOrDefault();
                if (pending == null) return;
                // During writing the ZIP handle is exclusive. During read-back its
                // complete manifest is readable and the previous backup is still live.
                try
                {
                    using var completed = ZipFile.OpenRead(pending);
                    if (completed.GetEntry("manifest.json") != null) cancelReadback.Cancel();
                }
                catch (IOException) { }
            });
            bool stopped = await Rejected(() => source.CreateBackupAsync(backup, progress, cancelReadback.Token));
            Check(stopped && cancelReadback.IsCancellationRequested && File.ReadAllBytes(backup).AsSpan().SequenceEqual(previous)
                && !Directory.EnumerateFiles(root, "roundtrip.swbackup.*.part").Any(), "Cancel during archive read-back preserves the previous backup");
        }
        foreach (string password in new[] { "DecoyBackup#123", "Backup#123" })
        {
            byte[] previous = File.ReadAllBytes(backup);
            bool changed = false;
            var progress = new InlineProgress(_ =>
            {
                if (changed) return;
                changed = true;
                source.Lock();
                if (!source.ApplyUnlock(source.VerifyPassword(password))) throw new Exception("Fixture unlock failed");
            });
            bool stopped = await Rejected(() => source.CreateBackupAsync(backup, progress));
            Check(changed && stopped && File.ReadAllBytes(backup).AsSpan().SequenceEqual(previous)
                && !Directory.EnumerateFiles(root, "roundtrip.swbackup.*.part").Any(),
                "Backup stops and preserves previous archive after " + (password == "Backup#123" ? "same-space re-unlock" : "switch to other space"));
            source.Lock();
            source.ApplyUnlock(source.VerifyPassword("Backup#123"));
        }

        string media = Path.Combine(dataRoot, "MediaVault", original.StoredFileName);
        byte[] encrypted = File.ReadAllBytes(media);
        byte[] lastGoodBackup = File.ReadAllBytes(backup);
        try
        {
            using (var truncated = File.OpenWrite(media)) truncated.SetLength(encrypted.Length - 1);
            bool stopped = await Rejected(() => source.CreateBackupAsync(backup));
            Check(stopped && File.ReadAllBytes(backup).AsSpan().SequenceEqual(lastGoodBackup),
                "Backup rejects a truncated source before replacing a working archive");
        }
        finally
        {
            File.WriteAllBytes(media, encrypted);
            File.WriteAllBytes(backup, lastGoodBackup);
        }

        foreach (string fault in new[] { "truncated payload", "invalid master salt", "missing decoy credentials", "incorrect catalog size", "duplicate item identifiers" })
        {
            string fixture = Path.Combine(root, fault.Replace(' ', '-') + ".swbackup");
            File.Copy(backup, fixture);
            if (fault == "truncated payload")
                ChangeEntry(fixture, "MediaVault/" + original.StoredFileName, bytes => bytes[..^1]);
            else
                ChangeEntry(fixture, "system_catalog.dat", bytes =>
                {
                    byte[] plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                    var config = JsonSerializer.Deserialize<VaultConfig>(plain)!;
                    if (fault == "invalid master salt") config.RealSaltHex = "";
                    else if (fault == "missing decoy credentials") config.DecoyHashHex = config.DecoySaltHex = "";
                    else if (fault == "incorrect catalog size") config.Items.Single().FileSizeBytes++;
                    else
                    {
                        var duplicate = config.DecoyItems.Single();
                        duplicate.Id = config.Items.Single().Id;
                        config.Items.Add(duplicate);
                        config.DecoyItems.Clear();
                    }
                    return ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(config), null, DataProtectionScope.CurrentUser);
                });
            var clean = new VaultService(Path.Combine(root, fault + "-target"));
            bool verifyFails = await Rejected(() => clean.VerifyBackupAsync(fixture));
            bool restoreFails = await Rejected(() => clean.RestoreBackupAsync(fixture));
            Check(verifyFails && restoreFails && !clean.HasVault, "Backup rejects checksum-consistent " + fault + " before restore commits");
        }
        string interruptedRoot = Path.Combine(root, "interrupted-target");
        var incomplete = new VaultService(interruptedRoot);
        string surviving = Path.Combine(interruptedRoot, "MediaVault", original.StoredFileName);
        File.Copy(media, surviving);
        Check(await Rejected(() => incomplete.InitializeVaultAsync("ShouldNotCreate#123")) && !incomplete.HasVault,
            "Setup preserves existing ciphertext when a catalog is missing");
        bool resumed = !await Rejected(() => incomplete.RestoreBackupAsync(backup));
        Check(resumed && incomplete.ApplyUnlock(incomplete.VerifyPassword("Backup#123")) &&
            File.ReadAllBytes(surviving).AsSpan().SequenceEqual(encrypted),
            "Restore resumes when a previous attempt already placed matching ciphertext");
        incomplete.Lock();
        foreach (string issue in new[] { "unrelated", "same-name" })
        {
            string location = Path.Combine(root, issue + "-target");
            var other = new VaultService(location);
            string existing = Path.Combine(location, "MediaVault", issue == "same-name" ? original.StoredFileName : "other.svault");
            byte[] different = (byte[])encrypted.Clone();
            different[^1] ^= 1;
            File.WriteAllBytes(existing, different);
            Check(await Rejected(() => other.RestoreBackupAsync(backup)) && !other.HasVault && File.ReadAllBytes(existing).AsSpan().SequenceEqual(different)
                && Directory.EnumerateFiles(Path.Combine(location, "MediaVault")).Count() == 1,
                "Restore preserves " + issue + " ciphertext and leaves target uncommitted");
        }

        byte[] catalogBytes = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(dataRoot, "system_catalog.dat")), null, DataProtectionScope.CurrentUser);
        var catalog = JsonSerializer.Deserialize<VaultConfig>(catalogBytes)!;
        byte[] master = CryptoService.DeriveKey("Backup#123", Convert.FromHexString(catalog.RealSaltHex), catalog.Iterations);
        try { SelfTest.WriteLegacyV1File(media, data, master); }
        finally { CryptographicOperations.ZeroMemory(master); CryptographicOperations.ZeroMemory(catalogBytes); }
        string mixed = Path.Combine(root, "legacy-and-current.swbackup");
        await source.CreateBackupAsync(mixed);
        var mixedDestination = new VaultService(Path.Combine(root, "legacy-and-current-target"));
        Check(await source.VerifyBackupAsync(mixed) == 2 && await mixedDestination.RestoreBackupAsync(mixed) == 2 &&
            mixedDestination.ApplyUnlock(mixedDestination.VerifyPassword("Backup#123")), "Backup verifies and restores mixed V1/V2 media");
        using (var legacy = mixedDestination.OpenDecryptedStream(mixedDestination.VisibleItems.Single()))
            Check(SHA256.HashData(legacy).AsSpan().SequenceEqual(SHA256.HashData(data)), "Restored multi-record V1 media decrypts to the original bytes");
        mixedDestination.Lock();
        source.Lock();
    }

    // Model damage present before a backup was hashed. The archive and its manifest agree.
    private static void ChangeEntry(string archivePath, string name, Func<byte[], byte[]> change)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        byte[] Read(string path)
        {
            using var stream = archive.GetEntry(path)!.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        void Replace(string path, byte[] bytes)
        {
            archive.GetEntry(path)!.Delete();
            using var stream = archive.CreateEntry(path, CompressionLevel.NoCompression).Open();
            stream.Write(bytes);
        }
        byte[] replacement = change(Read(name));
        var manifest = JsonNode.Parse(Read("manifest.json"))!;
        var record = manifest["Files"]!.AsArray().Single(file => (string?)file!["Name"] == name)!;
        record["Length"] = replacement.Length;
        record["Sha256"] = Convert.ToHexString(SHA256.HashData(replacement));
        Replace(name, replacement);
        Replace("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest));
    }
}
