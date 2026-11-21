using System.Security.Cryptography;
using System.Text.Json;
using StealthWorkspace.Models;

namespace StealthWorkspace.Services;

public partial class VaultService
{
    private string RemovalJournalPath => Path.Combine(_vaultRoot, "remove.dat");

    // Durable user intent is written before changing any payload. Failed removals remain
    // indexed; a crash after removal but before saving the index can be completed safely.
    private sealed class RemovalJournal
    {
        public int Version { get; set; } = 1;
        public string Operation { get; set; } = "";
        public string RealHashHex { get; set; } = "";
        public string DecoyHashHex { get; set; } = "";
        public List<string> Files { get; set; } = new();
    }

    private RemovalJournal ReadRemovalJournal()
    {
        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(RemovalJournalPath), null, DataProtectionScope.CurrentUser);
        try
        {
            var journal = JsonSerializer.Deserialize<RemovalJournal>(plain);
            if (journal == null || journal.Version != 1 || journal.Operation is not ("real-item" or "decoy-item" or "clear-decoy" or "reset")
                || journal.Files == null || journal.Files.Any(name => !IsSafeFileName(name))
                || journal.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Count)
                throw new InvalidDataException("删除恢复记录无效，现有文件已保留。");
            return journal;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private void WriteRemovalJournal(RemovalJournal journal)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(journal);
        try
        {
            byte[] cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            string temporary = RemovalJournalPath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(cipher);
                stream.Flush(true);
            }
            File.Move(temporary, RemovalJournalPath, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    // File.Exists also returns false on denied access. Only definite absence permits
    // removing an index entry or declaring a reset complete.
    private static bool IsDefinitelyMissing(string path)
    {
        try { _ = File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }

    private RemovalJournal BeginRemoval(string operation, List<string> files)
    {
        if (!IsDefinitelyMissing(RemovalJournalPath))
        {
            var existing = ReadRemovalJournal();
            ValidateRemovalJournal(existing);
            var remaining = existing.Files
                .Where(name => !IsDefinitelyMissing(Path.Combine(_mediaFolder, name)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (existing.Operation == "decoy-item" && operation == "clear-decoy")
            {
                existing.Operation = "clear-decoy";
                existing.Files = files;
                ValidateRemovalJournal(existing);
                WriteRemovalJournal(existing);
                return existing;
            }

            // Clear-decoy retries or subset retries of previously authorized items may proceed.
            bool matchesExisting = existing.Operation == operation &&
                (operation == "clear-decoy"
                 || existing.Files.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(files)
                 || remaining.SetEquals(files)
                 || (files.Count > 0 && files.All(remaining.Contains)));
            if (!matchesExisting)
                throw new InvalidOperationException("请先重试尚未完成的删除操作，再进行其他删除。");
            return existing;
        }
        RemovalJournal journal;
        lock (_sync)
            journal = new RemovalJournal { Operation = operation, Files = files,
                RealHashHex = _config!.RealHashHex, DecoyHashHex = _config.DecoyHashHex };
        ValidateRemovalJournal(journal);
        WriteRemovalJournal(journal);
        return journal;
    }

    private void ValidateRemovalJournal(RemovalJournal journal)
    {
        lock (_sync)
        {
            if (_corrupted || _config == null || journal.Operation == "reset"
                || !IsHexHash(journal.RealHashHex) || journal.RealHashHex != _config.RealHashHex)
                throw new InvalidDataException("删除记录与当前保险箱状态不一致，原文件已保留。");
            bool decoy = journal.Operation != "real-item";
            var references = new HashSet<string>((decoy ? _config.DecoyItems : _config.Items).Select(item => item.StoredFileName), StringComparer.OrdinalIgnoreCase);
            if ((decoy ? _config.Items : _config.DecoyItems).Any(item => journal.Files.Contains(item.StoredFileName, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("删除记录与另一个保险箱空间的媒体冲突。");
            bool allGone = journal.Files.All(name => IsDefinitelyMissing(Path.Combine(_mediaFolder, name)));
            if (decoy && journal.DecoyHashHex != _config.DecoyHashHex
                && !(journal.Operation == "clear-decoy" && allGone && _config.DecoyItems.Count == 0 && _config.DecoyHashHex.Length == 0))
                throw new InvalidDataException("诱饵空间已改变，删除记录未执行。");
            if (journal.Files.Any(name => !IsSafeFileName(name) || (!references.Contains(name) && !IsDefinitelyMissing(Path.Combine(_mediaFolder, name)))))
                throw new InvalidDataException("删除记录包含未授权的文件，原文件已保留。");
            if (journal.Operation == "clear-decoy" && references.Except(journal.Files, StringComparer.OrdinalIgnoreCase).Any())
                throw new InvalidDataException("诱饵空间包含不在删除记录中的文件，原文件已保留。");
        }
    }

    private async Task ExecuteRemovalAsync(RemovalJournal journal, IProgress<double>? progress = null, IReadOnlyList<string>? requested = null)
    {
        ValidateRemovalJournal(journal);
        var files = requested ?? journal.Files;
        await Task.Run(() =>
        {
            for (int i = 0; i < files.Count; i++)
            {
                string path = Path.Combine(_mediaFolder, files[i]);
                // Failure is retained in the index, even if another item succeeded.
                if (!IsDefinitelyMissing(path) && !CryptoService.ShredWorkspaceFile(path, 1))
                    AppLog.Warn("保险箱删除未完成，已保留索引和重试记录");
                progress?.Report((i + 1.0) / files.Count);
            }
        });
        bool complete;
        try { complete = FinalizeRemovalIndex(journal); }
        finally { RefreshVisibleItems(); }
        if (!complete && files.Any(name => !IsDefinitelyMissing(Path.Combine(_mediaFolder, name))))
            throw new IOException("部分文件未能移除，记录仍保留。请关闭占用的播放器或预览后，重试本次删除。");
    }

    private bool FinalizeRemovalIndex(RemovalJournal journal)
    {
        ValidateRemovalJournal(journal);
        var missing = new HashSet<string>(journal.Files.Where(name => IsDefinitelyMissing(Path.Combine(_mediaFolder, name))), StringComparer.OrdinalIgnoreCase);
        bool complete = missing.Count == journal.Files.Count;
        lock (_sync)
        {
            var config = _config!;
            var oldItems = config.Items;
            var oldDecoy = config.DecoyItems;
            string oldSalt = config.DecoySaltHex, oldHash = config.DecoyHashHex;
            int oldIterations = config.DecoyIterations;
            if (journal.Operation == "real-item") config.Items = oldItems.Where(item => !missing.Contains(item.StoredFileName)).ToList();
            else config.DecoyItems = oldDecoy.Where(item => !missing.Contains(item.StoredFileName)).ToList();
            if (journal.Operation == "clear-decoy" && complete)
            {
                config.DecoySaltHex = config.DecoyHashHex = "";
                config.DecoyIterations = 0;
            }
            try { SaveConfigOrThrow(); }
            catch
            {
                config.Items = oldItems; config.DecoyItems = oldDecoy;
                config.DecoySaltHex = oldSalt; config.DecoyHashHex = oldHash; config.DecoyIterations = oldIterations;
                throw new IOException("删除索引尚未保存，恢复记录已保留。请重新打开保险箱完成恢复。");
            }
        }
        // An old fallback index must not resurrect references to deleted payloads.
        File.Copy(_configPath, _configBackupPath, overwrite: true);
        if (complete) File.Delete(RemovalJournalPath);
        return complete;
    }

    private void RecoverPendingRemoval()
    {
        try
        {
            if (IsDefinitelyMissing(RemovalJournalPath)) return;
            var journal = ReadRemovalJournal();
            if (journal.Operation == "reset") { MarkResetIncomplete(); return; }
            if (_primaryReadDeferred || !IsDefinitelyMissing(_rotationJournalPath)) return;
            lock (_sync)
                if (_loadedFromBackup && !_corrupted) ValidateBackupMedia(Array.Empty<string>(), journal.Files);
            // Recovery only reconciles confirmed missing files. It never starts a new wipe.
            FinalizeRemovalIndex(journal);
        }
        catch (Exception ex)
        {
            AppLog.Warn("删除恢复尚未完成，索引、媒体和恢复记录均已保留", ex);
            lock (_sync)
            {
                if (_config == null || !IsHexHash(_config.RealHashHex))
                {
                    _corrupted = true;
                    _loadError = "删除或重置记录尚未恢复，不能初始化覆盖。现有文件已保留，可检查后再次重置。";
                }
            }
        }
    }

    private void MarkResetIncomplete()
    {
        lock (_sync)
        {
            _corrupted = true;
            _loadError = "上次重置尚未完成。请关闭占用文件的程序后再次重置；完成前不能初始化或恢复备份。";
        }
    }

    private void PersistLegacyImportDisabled()
    {
        string marker = Path.Combine(_vaultRoot, AppPaths.LegacyImportDisabledFileName);
        if (!SafeFileSystem.IsPlainWorkspacePath(_vaultRoot) ||
            (!IsDefinitelyMissing(marker) && !SafeFileSystem.IsPlainWorkspacePath(marker)))
            throw new IOException("无法安全保存重置状态，保险箱内容尚未移除。");
        // This tombstone intentionally survives reset and migration of the data root.
        // Persist it before deleting anything so a preserved legacy copy cannot return.
        using var stream = new FileStream(marker, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        stream.WriteByte(1);
        stream.SetLength(1);
        stream.Flush(true);
    }

    private async Task ResetWithJournalAsync()
    {
        // Reset is explicit authorization to remove all files in this vault's media root,
        // including preserved orphan ciphertext. Never recurse into unexpected directories.
        if (!SafeFileSystem.IsPlainWorkspacePath(_mediaFolder)) throw new IOException("媒体目录不可安全访问，重置未执行。");
        string[] entries = Directory.GetFileSystemEntries(_mediaFolder);
        if (entries.Any(path => !SafeFileSystem.IsPlainWorkspacePath(path) || (File.GetAttributes(path) & FileAttributes.Directory) != 0))
            throw new IOException("媒体目录包含链接或子目录，重置未执行。请先检查目录。");
        WriteRemovalJournal(new RemovalJournal { Operation = "reset", Files = entries.Select(Path.GetFileName).Select(name => name!).ToList() });
        MarkResetIncomplete();
        try
        {
            if (WebsiteIdentityForReset() is { } identity)
                await BrowserAccountDirectory.RemoveForReplacementAsync(_vaultRoot, identity);
            await Task.Run(() =>
            {
                PersistLegacyImportDisabled();
                foreach (string file in entries)
                    if (!IsDefinitelyMissing(file) && !CryptoService.ShredWorkspaceFile(file, 1))
                        throw new IOException("重置未完成：媒体文件仍被占用或无法移除。关闭占用程序后可再次重置。");
                if (Directory.EnumerateFileSystemEntries(_mediaFolder).Any())
                    throw new IOException("重置未完成：媒体目录仍非空。");
                foreach (string path in new[] { _configBackupPath, _configPath, _rotationJournalPath,
                    _configPath + ".tmp", _rotationJournalPath + ".tmp", RemovalJournalPath + ".tmp" })
                    if (!IsDefinitelyMissing(path) && !CryptoService.ShredWorkspaceFile(path, 1))
                        throw new IOException("重置未完成：保险箱配置仍被占用或无法移除。请稍后重试。");
                File.Delete(RemovalJournalPath); // Last: until this succeeds, reset is visibly incomplete.
            });
            lock (_sync)
            {
                _config = new VaultConfig();
                _corrupted = _loadedFromBackup = _primaryReadDeferred = false;
                _loadError = "";
            }
        }
        finally { RefreshVisibleItems(); }
    }
}
