using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace StealthWorkspace.Services;

public partial class VaultService
{
    private sealed record BackupEntry(string Name, long Length, string Sha256);
    private sealed record BackupManifest(int Version, DateTime CreatedUtc, List<BackupEntry> Files);

    // Backups contain the DPAPI-protected catalog and original encrypted media only.
    // File handles pin a coherent snapshot while imports/rotations may continue.
    public Task CreateBackupAsync(string destination, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        string target = Path.GetFullPath(destination);
        if (IsInsideVault(target)) throw new InvalidOperationException("请选择保险箱目录以外的位置保存备份。");
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".part";
        var pinned = new List<(string Name, FileStream Stream)>();
        byte[] catalog;
        try
        {
            lock (_sync)
            {
                EnsureUnlocked(requireReal: true);
                if (File.Exists(_rotationJournalPath)) throw new InvalidOperationException("请等待密码更换完成后再备份。");
                byte[] plain = JsonSerializer.SerializeToUtf8Bytes(_config!);
                try { catalog = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                foreach (string name in _config!.Items.Concat(_config.DecoyItems).Select(i => i.StoredFileName).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!IsSafeFileName(name)) throw new InvalidDataException("保险箱索引包含无效文件名。");
                    pinned.Add(("MediaVault/" + name, new FileStream(Path.Combine(_mediaFolder, name), FileMode.Open,
                        FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan)));
                }
            }

            var entries = new List<BackupEntry>();
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                using var catalogStream = new MemoryStream(catalog, writable: false);
                entries.Add(await WriteBackupEntryAsync(archive, "system_catalog.dat", catalogStream, cancellationToken));
                for (int i = 0; i < pinned.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsUnlocked) throw new OperationCanceledException("保险箱已锁定，备份已取消。");
                    entries.Add(await WriteBackupEntryAsync(archive, pinned[i].Name, pinned[i].Stream, cancellationToken));
                    progress?.Report((i + 1.0) / Math.Max(1, pinned.Count));
                }
                using Stream manifest = archive.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open();
                await JsonSerializer.SerializeAsync(manifest, new BackupManifest(1, DateTime.UtcNow, entries), cancellationToken: cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsUnlocked) throw new OperationCanceledException("保险箱已锁定，备份已取消。");
            File.Move(temporary, target, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            foreach (var file in pinned) file.Stream.Dispose();
            TryDelete(temporary);
        }
    }, cancellationToken);

    private static async Task<BackupEntry> WriteBackupEntryAsync(ZipArchive archive, string name, Stream source, CancellationToken token)
    {
        using Stream destination = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
        var (length, hash) = await CopyAndHashAsync(source, destination, source.Length, token);
        return new BackupEntry(name, length, hash);
    }

    public Task<int> VerifyBackupAsync(string source, CancellationToken token = default) =>
        ReadBackupAsync(source, null, token);

    // Restore only into an empty vault: an existing vault is never silently replaced.
    public async Task<int> RestoreBackupAsync(string source, CancellationToken token = default)
    {
        EnsureEmptyRestoreTarget();
        string staging = Path.Combine(_vaultRoot, ".restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var moved = new List<string>();
        bool committed = false;
        try
        {
            int count = await ReadBackupAsync(source, staging, token);
            token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                EnsureEmptyRestoreTarget();
                Directory.CreateDirectory(_mediaFolder);
                string stagedMedia = Path.Combine(staging, "MediaVault");
                if (Directory.Exists(stagedMedia))
                    foreach (string file in Directory.EnumerateFiles(stagedMedia))
                    {
                        string target = Path.Combine(_mediaFolder, Path.GetFileName(file));
                        File.Move(file, target);
                        moved.Add(target);
                    }
                // Commit last. Before this rename the target has no usable catalog.
                File.Move(Path.Combine(staging, "system_catalog.dat"), _configPath);
                committed = true;
                LoadConfig();
            }
            return count;
        }
        finally
        {
            if (!committed) foreach (string path in moved) TryDelete(path);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private void EnsureEmptyRestoreTarget()
    {
        lock (_sync)
        {
            if (HasVault || IsUnlocked || File.Exists(_configPath) || File.Exists(_configBackupPath) ||
                File.Exists(_rotationJournalPath) || (Directory.Exists(_mediaFolder) && Directory.EnumerateFileSystemEntries(_mediaFolder).Any()))
                throw new InvalidOperationException("恢复需要空保险箱，已有数据不会被覆盖。请保留现有保险箱后，在首次设置页面恢复备份。");
        }
    }

    private static Task<int> ReadBackupAsync(string path, string? staging, CancellationToken token) => Task.Run(async () =>
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("缺少备份清单。");
        if (manifestEntry.Length > 32 * 1024 * 1024) throw new InvalidDataException("备份清单过大。");
        BackupManifest manifest;
        using (Stream stream = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, cancellationToken: token)
                ?? throw new InvalidDataException("备份清单无效。");
        if (manifest.Version != 1 || manifest.Files == null || manifest.Files.Count == 0 || manifest.Files.Count > 100_000)
            throw new InvalidDataException("不支持的备份版本或文件数量。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            string leaf = entry.Name?.StartsWith("MediaVault/", StringComparison.Ordinal) == true ? entry.Name[11..] : "";
            if (entry.Name != "system_catalog.dat" && (!IsSafeFileName(leaf) || !leaf.EndsWith(".svault", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("备份包含不允许的路径。");
            if (!names.Add(entry.Name!) || entry.Length < 0 || entry.Sha256?.Length != 64)
                throw new InvalidDataException("备份清单存在重复或无效记录。");
        }
        if (!names.Contains("system_catalog.dat") || archive.Entries.Count != names.Count + 1 ||
            archive.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count)
            throw new InvalidDataException("备份文件与清单不一致。");

        VaultConfig? config = null;
        foreach (BackupEntry entry in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            var zipEntry = archive.GetEntry(entry.Name) ?? throw new InvalidDataException("备份缺少媒体文件。");
            if (zipEntry.Length != entry.Length) throw new InvalidDataException("备份文件长度不一致。");
            bool isCatalog = entry.Name == "system_catalog.dat";
            if (isCatalog && entry.Length > 32 * 1024 * 1024) throw new InvalidDataException("备份索引过大。");
            string? target = staging == null ? null : Path.Combine(staging, entry.Name.Replace('/', Path.DirectorySeparatorChar));
            if (target != null) Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using Stream source = zipEntry.Open();
            using Stream destination = isCatalog ? new MemoryStream() : target == null ? Stream.Null :
                new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            var (length, hash) = await CopyAndHashAsync(source, destination, entry.Length, token);
            if (length != entry.Length || !string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份完整性校验失败，文件可能损坏。");
            if (isCatalog)
            {
                byte[] encrypted = ((MemoryStream)destination).ToArray();
                byte[] plain;
                try { plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser); }
                catch (CryptographicException) { throw new InvalidDataException("此备份只能由原 Windows 用户恢复，或备份索引已经损坏。"); }
                try { config = JsonSerializer.Deserialize<VaultConfig>(plain); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                if (target != null) await File.WriteAllBytesAsync(target, encrypted, token);
            }
        }
        if (config == null || !IsHexHash(config.RealHashHex) || config.Items == null || config.DecoyItems == null)
            throw new InvalidDataException("备份中的保险箱索引无效。");
        var items = config.Items.Concat(config.DecoyItems).ToArray();
        if (items.Any(i => i == null || !IsSafeFileName(i.StoredFileName)) ||
            items.Select(i => "MediaVault/" + i.StoredFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length ||
            !names.SetEquals(items.Select(i => "MediaVault/" + i.StoredFileName).Append("system_catalog.dat")))
            throw new InvalidDataException("媒体文件与保险箱索引不匹配。");
        return items.Length;
    }, token);

    private static async Task<(long Length, string Hash)> CopyAndHashAsync(Stream source, Stream destination, long expected, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            total = checked(total + count);
            if (total > expected) throw new InvalidDataException("备份文件超出声明长度。");
            hash.AppendData(buffer, 0, count);
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return (total, Convert.ToHexString(hash.GetHashAndReset()));
    }
}
