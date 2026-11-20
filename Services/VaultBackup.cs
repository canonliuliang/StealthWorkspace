using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using StealthWorkspace.Models;

namespace StealthWorkspace.Services;

public partial class VaultService
{
    private sealed record BackupEntry(string Name, long Length, string Sha256);
    private sealed record BackupManifest(int Version, DateTime CreatedUtc, List<BackupEntry> Files);

    // Backups contain the DPAPI-protected catalog and original encrypted media only.
    // File handles pin a coherent snapshot while imports/rotations may continue.
    public Task CreateBackupAsync(string destination, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) => CreateVerifiedBackupAsync(destination, progress, cancellationToken);

    internal Task<int> CreateVerifiedBackupAsync(string destination, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        long epoch = CaptureUnlockEpoch();
        return Task.Run(() => CreateBackupCoreAsync(destination, epoch, progress, cancellationToken), cancellationToken);
    }

    internal Task<int> ExportBookmarksAsync(string destination, IReadOnlyList<BookmarkItem> bookmarks, CancellationToken token)
    {
        long epoch = CaptureUnlockEpoch();
        return Task.Run(() =>
        {
            string target = Path.GetFullPath(destination);
            ValidatePersonalExport(target, epoch, token);
            return BookmarkHtml.Write(target, bookmarks, token, (temp, final) => CommitPersonalExport(temp, final, epoch, token));
        }, token);
    }

    private void ValidatePersonalExport(string target, long epoch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrentUnlockSession(epoch, decoy: false)) throw new OperationCanceledException("保险箱会话已改变，导出已取消。");
        if (IsInsideVault(target) || IsInsideVault(Path.GetDirectoryName(target) ?? target))
            throw new InvalidDataException("请选择保险箱目录以外的位置保存文件。");
    }

    private void CommitPersonalExport(string temporary, string target, long epoch, CancellationToken token)
    {
        lock (_sync)
        {
            ValidatePersonalExport(target, epoch, token);
            File.Move(temporary, target, overwrite: true);
        }
    }

    private async Task<int> CreateBackupCoreAsync(string destination, long epoch, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string target = Path.GetFullPath(destination);
        ValidatePersonalExport(target, epoch, cancellationToken);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".part";
        var pinned = new List<(VaultItem Item, FileStream Stream)>();
        byte[] catalog;
        try
        {
            // Exclude catalog mutations while pinning, without holding the state lock
            // needed by the lock button and the system session-lock handler.
            using (await EnterMutationAsync(cancellationToken))
            {
                lock (_sync)
                {
                    EnsureUnlocked(requireReal: true);
                    RequireUnlockEpoch(epoch);
                }
                byte[] plain = JsonSerializer.SerializeToUtf8Bytes(_config!);
                try { catalog = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                foreach (var item in ReadBackupItems(_config!).Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentUnlockSession(epoch, decoy: false)) throw new OperationCanceledException("保险箱会话已改变，备份已取消。");
                    string path = Path.Combine(_mediaFolder, item.StoredFileName);
                    if (!SafeFileSystem.IsPlainPath(path)) throw new InvalidDataException("保险箱媒体不可安全读取。");
                    pinned.Add((item, new FileStream(path, FileMode.Open,
                        FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan)));
                }
            }

            var entries = new List<BackupEntry>();
            double totalBytes = catalog.LongLength + pinned.Sum(file => (double)file.Stream.Length);
            long copied = 0;
            void Report(double fraction)
            {
                if (!IsCurrentUnlockSession(epoch, decoy: false)) throw new OperationCanceledException("保险箱会话已改变，备份已取消。");
                progress?.Report(fraction);
            }
            void ReportBytes(int written)
            {
                copied += written;
                Report(0.5 * copied / totalBytes);
            }
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                using var catalogStream = new MemoryStream(catalog, writable: false);
                entries.Add(await WriteBackupEntryAsync(archive, "system_catalog.dat", catalogStream, cancellationToken, ReportBytes));
                foreach (var (item, stream) in pinned)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateStoredLength(item, CryptoService.ReadHeader(stream).PlainLength);
                    stream.Position = 0;
                    entries.Add(await WriteBackupEntryAsync(archive, "MediaVault/" + Path.GetFileName(stream.Name), stream, cancellationToken, ReportBytes));
                }
                using Stream manifest = archive.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open();
                await JsonSerializer.SerializeAsync(manifest, new BackupManifest(1, DateTime.UtcNow, entries), cancellationToken: cancellationToken);
            }
            // Release the source snapshot during read-back. The catalog and media
            // being verified now belong to the closed archive on the target volume.
            foreach (var file in pinned) file.Stream.Dispose();
            pinned.Clear();
            int count = await ReadBackupAsync(temporary, null, cancellationToken, fraction => Report(0.5 + 0.5 * fraction));
            CommitPersonalExport(temporary, target, epoch, cancellationToken);
            progress?.Report(1);
            return count;
        }
        finally
        {
            foreach (var file in pinned) file.Stream.Dispose();
            TryDelete(temporary);
        }
    }

    private static async Task<BackupEntry> WriteBackupEntryAsync(ZipArchive archive, string name, Stream source, CancellationToken token, Action<int> reportBytes)
    {
        using Stream destination = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
        var (length, hash) = await CopyAndHashAsync(source, destination, source.Length, token, reportBytes);
        return new BackupEntry(name, length, hash);
    }

    public Task<int> VerifyBackupAsync(string source, CancellationToken token = default, IProgress<double>? progress = null) =>
        ReadBackupAsync(source, null, token, progress == null ? null : progress.Report);

    // A restore interrupted before its catalog commit may have placed some ciphertext.
    // It can be resumed only with a backup that accounts for every existing byte.
    public async Task<int> RestoreBackupAsync(string source, CancellationToken token = default)
    {
        using var operation = await EnterMutationAsync(token);
        EnsureRestoreTargetHasNoCatalog();
        string staging = Path.Combine(_vaultRoot, ".restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var media = new Dictionary<string, BackupEntry>(StringComparer.OrdinalIgnoreCase);
        var matching = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        var moved = new List<string>();
        bool committed = false;
        try
        {
            int count = await ReadBackupAsync(source, staging, token, verifiedMedia: media);
            foreach (string existing in Directory.EnumerateFileSystemEntries(_mediaFolder))
            {
                string name = Path.GetFileName(existing);
                if (!media.TryGetValue(name, out var entry) || !SafeFileSystem.IsPlainPath(existing) || Directory.Exists(existing))
                    throw new InvalidOperationException("媒体目录含有本备份之外的内容，恢复已停止。请保留原件并检查目录。");
                var held = new FileStream(existing, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
                if (!matching.TryAdd(name, held))
                {
                    held.Dispose();
                    throw new InvalidDataException("媒体目录存在名称冲突，原文件已保留。");
                }
                if (held.Length != entry.Length ||
                    !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(held, token)), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("已有媒体与备份中的同名文件不同，原文件已保留。");
            }
            token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                EnsureRestoreTargetHasNoCatalog();
                if (Directory.EnumerateFileSystemEntries(_mediaFolder).Any(path => !matching.ContainsKey(Path.GetFileName(path))))
                    throw new IOException("恢复期间媒体目录已改变，请重试。");
                string stagedMedia = Path.Combine(staging, "MediaVault");
                if (Directory.Exists(stagedMedia))
                    foreach (string file in Directory.EnumerateFiles(stagedMedia))
                    {
                        token.ThrowIfCancellationRequested();
                        string name = Path.GetFileName(file);
                        if (matching.ContainsKey(name)) continue;
                        string target = Path.Combine(_mediaFolder, name);
                        File.Move(file, target);
                        moved.Add(target);
                    }
                // Commit last. Before this rename the target has no usable catalog.
                token.ThrowIfCancellationRequested();
                File.Move(Path.Combine(staging, "system_catalog.dat"), _configPath);
                committed = true;
                LoadConfig();
            }
            return count;
        }
        finally
        {
            foreach (var stream in matching.Values) stream.Dispose();
            if (!committed) foreach (string path in moved) TryDelete(path);
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("恢复临时目录尚未清除", ex); }
        }
    }

    private void EnsureRestoreTargetHasNoCatalog()
    {
        lock (_sync)
        {
            if (HasVault || IsUnlocked || !IsDefinitelyMissing(_configPath) ||
                !IsDefinitelyMissing(_configBackupPath) || !IsDefinitelyMissing(_rotationJournalPath) || !IsDefinitelyMissing(RemovalJournalPath))
                throw new InvalidOperationException("恢复需要空保险箱，已有数据不会被覆盖。请保留现有保险箱后，在首次设置页面恢复备份。");
            EnsureMediaDirectory();
        }
    }

    private static Task<int> ReadBackupAsync(string path, string? staging, CancellationToken token, Action<double>? reportProgress = null,
        Dictionary<string, BackupEntry>? verifiedMedia = null) => Task.Run(async () =>
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
            if (entry is null) throw new InvalidDataException("备份清单包含空记录。");
            string leaf = entry.Name?.StartsWith("MediaVault/", StringComparison.Ordinal) == true ? entry.Name[11..] : "";
            if (entry.Name != "system_catalog.dat" && (!IsSafeFileName(leaf) || !leaf.EndsWith(".svault", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("备份包含不允许的路径。");
            if (!names.Add(entry.Name!) || entry.Length < 0 || entry.Sha256?.Length != 64)
                throw new InvalidDataException("备份清单存在重复或无效记录。");
        }
        if (!names.Contains("system_catalog.dat") || archive.Entries.Count != names.Count + 1 ||
            archive.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count)
            throw new InvalidDataException("备份文件与清单不一致。");

        Dictionary<string, VaultItem>? items = null;
        double totalBytes = Math.Max(1, manifest.Files.Sum(file => (double)file.Length));
        long verifiedBytes = 0;
        void ReportBytes(int read)
        {
            verifiedBytes += read;
            reportProgress?.Invoke(verifiedBytes / totalBytes);
        }
        // Validate the catalog first even for older archives with a different entry order.
        var catalogEntry = manifest.Files.Single(file => file.Name == "system_catalog.dat");
        foreach (BackupEntry entry in manifest.Files.Where(file => file != catalogEntry).Prepend(catalogEntry))
        {
            token.ThrowIfCancellationRequested();
            var zipEntry = archive.GetEntry(entry.Name) ?? throw new InvalidDataException("备份缺少媒体文件。");
            if (zipEntry.Length != entry.Length) throw new InvalidDataException("备份文件长度不一致。");
            bool isCatalog = entry.Name == "system_catalog.dat";
            if (isCatalog && entry.Length > 32 * 1024 * 1024) throw new InvalidDataException("备份索引过大。");
            if (!isCatalog)
            {
                using var header = zipEntry.Open();
                ValidateStoredLength(items![entry.Name], CryptoService.ReadHeader(header, entry.Length, token).PlainLength);
            }
            string? target = staging == null ? null : Path.Combine(staging, entry.Name.Replace('/', Path.DirectorySeparatorChar));
            if (target != null) Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using Stream source = zipEntry.Open();
            using Stream destination = isCatalog ? new MemoryStream() : target == null ? Stream.Null :
                new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            var (length, hash) = await CopyAndHashAsync(source, destination, entry.Length, token, ReportBytes);
            if (length != entry.Length || !string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份完整性校验失败，文件可能损坏。");
            if (!isCatalog) verifiedMedia?.Add(items![entry.Name].StoredFileName, entry);
            if (isCatalog)
            {
                byte[] encrypted = ((MemoryStream)destination).ToArray();
                byte[] plain;
                try { plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser); }
                catch (CryptographicException) { throw new InvalidDataException("此备份只能由原 Windows 用户恢复，或备份索引已经损坏。"); }
                try { items = ReadBackupItems(JsonSerializer.Deserialize<VaultConfig>(plain)); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                if (!names.SetEquals(items.Keys.Append("system_catalog.dat"))) throw new InvalidDataException("媒体文件与保险箱索引不匹配。");
                if (target != null) await File.WriteAllBytesAsync(target, encrypted, token);
            }
        }
        return items!.Count;
    }, token);

    private static Dictionary<string, VaultItem> ReadBackupItems(VaultConfig? config)
    {
        if (config == null || !IsHexHash(config.RealHashHex) || !IsHexSalt(config.RealSaltHex) || config.Items == null || config.DecoyItems == null)
            throw new InvalidDataException("备份中的保险箱索引无效。");
        if ((config.DecoyItems.Count != 0 || !string.IsNullOrEmpty(config.DecoyHashHex) || !string.IsNullOrEmpty(config.DecoySaltHex))
            && (!IsHexHash(config.DecoyHashHex) || !IsHexSalt(config.DecoySaltHex)))
            throw new InvalidDataException("备份中的诱饵空间缺少密码校验信息。");
        var files = new Dictionary<string, VaultItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var space in new[] { config.Items, config.DecoyItems })
        {
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in space)
                if (item == null || item.FileSizeBytes < 0 || !IsSafeFileName(item.StoredFileName) ||
                    !item.StoredFileName.EndsWith(".svault", StringComparison.OrdinalIgnoreCase) ||
                    !files.TryAdd("MediaVault/" + item.StoredFileName, item) ||
                    (!string.IsNullOrWhiteSpace(item.Id) && !identifiers.Add(item.Id)))
                    throw new InvalidDataException("媒体文件与保险箱索引不匹配。");
        }
        return files;
    }

    private static async Task<(long Length, string Hash)> CopyAndHashAsync(Stream source, Stream destination, long expected, CancellationToken token, Action<int> reportBytes)
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
            reportBytes(count);
        }
        return (total, Convert.ToHexString(hash.GetHashAndReset()));
    }
}
