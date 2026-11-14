using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace StealthWorkspace.Services;

internal static class BackupSelfTest
{
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
        source.Lock();
    }
}
