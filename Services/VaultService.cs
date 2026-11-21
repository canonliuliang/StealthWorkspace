using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StealthWorkspace.Models;

namespace StealthWorkspace.Services;

public class VaultConfig
{
    public int Version { get; set; } = 2;
    public string RealSaltHex { get; set; } = string.Empty;
    public string RealHashHex { get; set; } = string.Empty;
    public int Iterations { get; set; } = CryptoService.LegacyIterations;
    public List<VaultItem> Items { get; set; } = new();

    public string DecoySaltHex { get; set; } = string.Empty;
    public string DecoyHashHex { get; set; } = string.Empty;
    public int DecoyIterations { get; set; }
    public List<VaultItem> DecoyItems { get; set; } = new();
}

/// <summary>
/// Two-space encrypted vault (real + optional decoy), stored under a camouflaged
/// per-user hidden directory.
///
/// Key management:
///  - The master password is stretched with PBKDF2-HMAC-SHA256 (600k iterations for new
///    vaults, per-vault iteration count persisted for future upgrades).
///  - Only SHA-256(key) is stored (inside a DPAPI protected config), never the key or
///    password.
///  - The derived key lives only in memory while unlocked and is zeroed on lock.
///  - V2 media files are additionally re-keyed per file with HKDF.
/// </summary>
public partial class VaultService
{
    private static VaultService? _instance;
    public static VaultService Instance => _instance ??= new VaultService();

    private const int MinPasswordLength = 6;

    private readonly string _vaultRoot;
    private readonly string _mediaFolder;
    private readonly string _configPath;
    private readonly string _configBackupPath;
    private readonly string _rotationJournalPath;
    private readonly string? _legacyMigrationSource;
    private readonly object _sync = new();

    private VaultConfig? _config;
    private byte[]? _currentKey;
    private bool _isUnlocked;
    private bool _isDecoyMode;
    private bool _corrupted;
    private bool _loadedFromBackup;
    private bool _primaryReadDeferred;
    private string _loadError = string.Empty;


    public VaultService() : this(null)
    {
    }

    /// <summary>Test/diagnostic constructor allowing an isolated data root.</summary>
    public VaultService(string? dataRootOverride) : this(dataRootOverride, null)
    {
    }

    /// <summary>Explicit legacy source used only by isolated migration fixtures.</summary>
    internal VaultService(string? dataRootOverride, string? legacyStore)
    {
        _vaultRoot = string.IsNullOrWhiteSpace(dataRootOverride) ? AppPaths.EnsureDataRoot() : dataRootOverride!;
        _mediaFolder = Path.Combine(_vaultRoot, "MediaVault");
        _configPath = string.IsNullOrWhiteSpace(dataRootOverride) ? AppPaths.VaultConfigPath : Path.Combine(_vaultRoot, "system_catalog.dat");
        _configBackupPath = _configPath + ".bak";
        _rotationJournalPath = Path.Combine(_vaultRoot, "rotate.dat");

        try
        {
            Directory.CreateDirectory(_vaultRoot);
            Directory.CreateDirectory(_mediaFolder);
            if (string.IsNullOrWhiteSpace(dataRootOverride))
            {
                AppPaths.Hide(_vaultRoot);
                AppPaths.Hide(_mediaFolder);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("初始化保险箱目录失败", ex);
        }

        if (legacyStore == null && string.IsNullOrWhiteSpace(dataRootOverride) && !AppPaths.HasDataRootOverride)
            legacyStore = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "StealthVault_Store");
        _legacyMigrationSource = legacyStore;
        LoadConfigWithLegacyMigration();
        RecoverPendingOperations();
    }

    #region Public state

    public bool IsUnlocked
    {
        get { lock (_sync) return _isUnlocked; }
    }

    public bool IsDecoyMode
    {
        get { lock (_sync) return _isUnlocked && _isDecoyMode; }
    }

    /// <summary>True when a real vault exists (or the config exists but is unreadable).</summary>
    public bool HasVault
    {
        get
        {
            lock (_sync)
            {
                return _corrupted || File.Exists(RemovalJournalPath) || (_config != null && IsHexHash(_config.RealHashHex));
            }
        }
    }

    public bool HasDecoyVault
    {
        get { lock (_sync) return _config != null && IsHexHash(_config.DecoyHashHex); }
    }

    /// <summary>True when the encrypted config existed but could not be read at all.</summary>
    public bool IsCorrupted
    {
        get { lock (_sync) return _corrupted; }
    }

    public string LoadError
    {
        get { lock (_sync) return _loadError; }
    }

    /// <summary>Re-reads the encrypted config from disk (used by the "retry" button after
    /// an I/O/decryption failure). Locks the vault first to drop any stale key.</summary>
    public void ReloadConfig()
    {
        Lock();
        if (!_mutationGate.Wait(0)) return;
        try { LoadConfigWithLegacyMigration(); RecoverPendingOperations(); }
        finally { _mutationGate.Release(); }
    }

    public ObservableCollection<VaultItem> VisibleItems { get; } = new();

    public event Action? Locked;
    public event Action? Unlocked;

    #endregion

    #region Vault creation / password management

    public async Task InitializeVaultAsync(string masterPassword, string? decoyPassword = null, IProgress<double>? progress = null)
    {
        long setupEpoch = CaptureUnlockEpoch();
        using var operation = await EnterMutationAsync();
        ValidateNewPassword(masterPassword, nameof(masterPassword));
        if (!string.IsNullOrEmpty(decoyPassword))
        {
            ValidateNewPassword(decoyPassword, nameof(decoyPassword));
            if (string.Equals(masterPassword, decoyPassword, StringComparison.Ordinal))
                throw new ArgumentException("主密码与诱饵密码不能相同。");
        }

        lock (_sync)
        {
            EnsureNewVaultTarget();
        }

        byte[] realKey = Array.Empty<byte>();
        byte[]? decoyKey = null;
        bool opened = false;
        try
        {
            progress?.Report(0.05);
            byte[] realSalt = CryptoService.GenerateRandomBytes(CryptoService.SaltSize);
            realKey = await Task.Run(() => CryptoService.DeriveKey(masterPassword, realSalt, CryptoService.DefaultIterations));
            progress?.Report(0.6);
            byte[]? decoySalt = null;
            if (!string.IsNullOrEmpty(decoyPassword))
            {
                decoySalt = CryptoService.GenerateRandomBytes(CryptoService.SaltSize);
                decoyKey = await Task.Run(() => CryptoService.DeriveKey(decoyPassword, decoySalt, CryptoService.DefaultIterations));
            }
            progress?.Report(0.9);
            lock (_sync)
            {
                EnsureNewVaultTarget();
                var candidate = new VaultConfig
                {
                    Version = 2,
                    RealSaltHex = Convert.ToHexString(realSalt),
                    RealHashHex = Convert.ToHexString(CryptoService.HashKey(realKey)),
                    Iterations = CryptoService.DefaultIterations,
                    Items = new List<VaultItem>()
                };
                if (decoySalt != null && decoyKey != null)
                {
                    candidate.DecoySaltHex = Convert.ToHexString(decoySalt);
                    candidate.DecoyHashHex = Convert.ToHexString(CryptoService.HashKey(decoyKey));
                    candidate.DecoyIterations = CryptoService.DefaultIterations;
                }
                // Publish only after the catalog write succeeds. A failed first
                // setup remains empty and retryable in the same application session.
                SaveConfigOrThrow(candidate);
                _config = candidate;
                _corrupted = false;
                _loadError = string.Empty;
                opened = _unlockEpoch == setupEpoch;
                if (opened)
                {
                    ReplaceCurrentKey(realKey);
                    realKey = Array.Empty<byte>();
                }
                _unlockEpoch++;
                _isUnlocked = opened;
                _isDecoyMode = false;
            }
            PublishVaultState(unlocked: opened, notify: opened);
            progress?.Report(1.0);
        }
        finally
        {
            if (realKey.Length > 0) CryptographicOperations.ZeroMemory(realKey);
            if (decoyKey != null) CryptographicOperations.ZeroMemory(decoyKey);
        }
    }

    /// <summary>
    /// Re-encrypts all real-vault media with a new master password. Only allowed while
    /// the real vault is unlocked. A rotation journal makes the operation crash safe:
    /// old files are removed only after the new config has been atomically committed.
    /// </summary>
    public async Task ChangeMasterPasswordAsync(string newPassword, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterMutationAsync(cancellationToken);
        ValidateNewPassword(newPassword, nameof(newPassword));
        EnsureUnlocked(requireReal: true);
        var items = SnapshotCurrentItems(out VaultConfig config, decoy: false);
        await RejectOtherSpacePasswordAsync(newPassword, config.DecoySaltHex, config.DecoyHashHex,
            config.DecoyIterations, cancellationToken);
        byte[] oldKey;
        lock (_sync)
        {
            EnsureUnlocked(requireReal: true);
            oldKey = CloneCurrentKey();
        }

        byte[] newSalt = CryptoService.GenerateRandomBytes(CryptoService.SaltSize);
        byte[]? newKey = null;
        try
        {
            progress?.Report(0.02);
            newKey = await Task.Run(() => CryptoService.DeriveKey(newPassword, newSalt, CryptoService.DefaultIterations), cancellationToken);
            await RotateAsync(config, items, decoy: false, oldKey, newKey, newSalt,
                CryptoService.DefaultIterations, progress, cancellationToken);

            RefreshVisibleItems();

            progress?.Report(1.0);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldKey);
            if (newKey != null) CryptographicOperations.ZeroMemory(newKey);
        }
    }

    /// <summary>
    /// Sets or replaces the decoy password. Requires the real vault to be unlocked.
    /// When a decoy vault already exists, <paramref name="oldDecoyPassword"/> must be
    /// supplied so its media can be re-encrypted with the new decoy key; otherwise the
    /// user must explicitly destroy the old decoy vault first (ClearDecoyAsync).
    /// </summary>
    public async Task SetDecoyPasswordAsync(
        string decoyPassword,
        string? oldDecoyPassword = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = await EnterMutationAsync(cancellationToken);
        ValidateNewPassword(decoyPassword, nameof(decoyPassword));
        EnsureUnlocked(requireReal: true);

        VaultConfig config;
        List<VaultItem> decoyItems;
        bool alreadyExists;
        string saltHex;
        string hashHex;
        int iterations;
        lock (_sync)
        {
            config = _config ?? throw new InvalidOperationException("保险箱配置未加载。");
            alreadyExists = IsHexHash(config.DecoyHashHex) && IsHexSalt(config.DecoySaltHex);
            decoyItems = config.DecoyItems.ToList();
            saltHex = config.DecoySaltHex;
            hashHex = config.DecoyHashHex;
            iterations = config.DecoyIterations > 0 ? config.DecoyIterations : CryptoService.DefaultIterations;
        }

        byte[] newSalt = CryptoService.GenerateRandomBytes(CryptoService.SaltSize);
        byte[]? newKey = null;
        byte[]? oldDecoyKey = null;

        try
        {
            await RejectOtherSpacePasswordAsync(decoyPassword, config.RealSaltHex, config.RealHashHex,
                config.Iterations, cancellationToken);
            newKey = await Task.Run(() => CryptoService.DeriveKey(decoyPassword, newSalt, CryptoService.DefaultIterations), cancellationToken);

            if (alreadyExists)
            {
                if (string.IsNullOrEmpty(oldDecoyPassword))
                    throw new InvalidOperationException("修改诱饵密码需要提供当前的诱饵密码。");

                oldDecoyKey = await Task.Run(() =>
                {
                    byte[] candidate = CryptoService.DeriveKey(oldDecoyPassword!, Convert.FromHexString(saltHex), iterations);
                    if (!CryptoService.VerifyKeyHash(candidate, hashHex))
                    {
                        CryptographicOperations.ZeroMemory(candidate);
                        throw new UnauthorizedAccessException("当前诱饵密码不正确。");
                    }
                    return candidate;
                }, cancellationToken);

            }
            await RotateAsync(config, decoyItems, decoy: true, oldDecoyKey ?? Array.Empty<byte>(), newKey,
                newSalt, CryptoService.DefaultIterations, progress, cancellationToken);

            progress?.Report(1.0);
        }
        finally
        {
            if (newKey != null) CryptographicOperations.ZeroMemory(newKey);
            if (oldDecoyKey != null) CryptographicOperations.ZeroMemory(oldDecoyKey);
        }
    }

    /// <summary>Removes the decoy vault and destroys its encrypted media.</summary>
    public async Task ClearDecoyAsync()
    {
        using var operation = await EnterMutationAsync(allowPendingRemoval: true);
        EnsureUnlocked(requireReal: true);
        List<string> files;
        lock (_sync) files = _config!.DecoyItems.Select(item => item.StoredFileName).ToList();
        await ExecuteRemovalAsync(BeginRemoval("clear-decoy", files));
    }

    public async Task ResetVaultAsync()
    {
        using var operation = await EnterMutationAsync(allowPendingRecovery: true, allowPendingRemoval: true);
        Lock();
        await ResetWithJournalAsync();
    }

    #endregion

    #region Import / Export / Delete

    public Task<VaultItem> ImportFileAsync(
        string sourcePath,
        bool shredOriginal,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        Action<bool>? reportOriginalRemoval = null) =>
        ImportPathAsync(sourcePath, shredOriginal, progress, cancellationToken, reportOriginalRemoval);

    internal Task<VaultItem> ImportDownloadAsync(string sourcePath, SafeShred.FileIdentity downloadIdentity,
        IProgress<double> progress, CancellationToken token, Action<bool> reportOriginalRemoval) =>
        ImportPathAsync(sourcePath, true, progress, token, reportOriginalRemoval, downloadIdentity);

    private async Task<VaultItem> ImportPathAsync(string sourcePath, bool shredOriginal,
        IProgress<double>? progress, CancellationToken cancellationToken, Action<bool>? reportOriginalRemoval,
        SafeShred.FileIdentity? downloadIdentity = null)
    {
        long epoch = CaptureUnlockEpoch();
        using var operation = await EnterMutationAsync(cancellationToken);
        lock (_sync) RequireUnlockEpoch(epoch);
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("文件路径为空。", nameof(sourcePath));

        string fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath)) throw new FileNotFoundException("源文件不存在。", fullSourcePath);
        if (downloadIdentity.HasValue)
        {
            string downloadDirectory = Path.Combine(_vaultRoot, "IsolatedDownloads");
            if (!string.Equals(Path.GetDirectoryName(fullSourcePath), downloadDirectory, StringComparison.OrdinalIgnoreCase)
                || !SafeFileSystem.IsPlainPath(fullSourcePath))
                throw new InvalidOperationException("下载文件位置已改变。");
        }
        else if (IsInsideVault(fullSourcePath)) throw new InvalidOperationException("不能把保险箱自身的文件再次导入。");

        using var digest = shredOriginal ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        SafeShred.FileIdentity? identity = null;
        VaultItem item;
        using (var input = new FileStream(fullSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.SequentialScan))
        {
            if (shredOriginal) identity = SafeShred.CaptureIdentity(input.SafeFileHandle);
            if (downloadIdentity.HasValue && identity != downloadIdentity)
                throw new IOException("原下载文件已被替换，导入已取消。");
            item = await ImportStreamCoreAsync(input, Path.GetFileName(fullSourcePath), epoch, progress, cancellationToken, digest);
        }
        RefreshVisibleItems();
        if (identity.HasValue && digest != null)
        {
            byte[] hash = digest.GetHashAndReset();
            try
            {
                bool CanRemove() => !cancellationToken.IsCancellationRequested && IsCurrentUnlockSession(epoch, item.IsDecoy);
                bool removed = CanRemove() && await Task.Run(() =>
                    CryptoService.ShredImportedFile(fullSourcePath, identity.Value, hash, CanRemove));
                reportOriginalRemoval?.Invoke(removed);
            }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }
        progress?.Report(1);
        return item;
    }

    internal async Task<VaultItem> ImportStreamAsync(Stream source, string fileName,
        IProgress<double> progress, CancellationToken token)
    {
        long epoch = CaptureUnlockEpoch();
        using var operation = await EnterMutationAsync(token);
        if (!IsSafeFileName(fileName)) throw new ArgumentException("图片名称无效。", nameof(fileName));
        var item = await ImportStreamCoreAsync(source, fileName, epoch, progress, token);
        RefreshVisibleItems();
        progress.Report(1);
        return item;
    }

    private async Task<VaultItem> ImportStreamCoreAsync(Stream source, string fileName, long epoch,
        IProgress<double>? progress, CancellationToken cancellationToken, IncrementalHash? digest = null)
    {
        bool decoy;
        byte[] key;
        lock (_sync)
        {
            RequireUnlockEpoch(epoch);
            key = CloneCurrentKey();
            decoy = _isDecoyMode;
        }
        string id = Guid.NewGuid().ToString("N");
        string destName = id + ".svault";
        string destPath = Path.Combine(_mediaFolder, destName);
        string tempPath = destPath + ".part";

        try
        {
            Directory.CreateDirectory(_mediaFolder);
            long length = source.Length - source.Position;
            progress?.Report(0);
            await Task.Run(() => CryptoService.EncryptStream(source, tempPath, key, progress, cancellationToken, digest), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Re-check state: the user might have locked the vault while encrypting.
            if (!IsCurrentUnlockSession(epoch, decoy))
            {
                TryDelete(tempPath);
                throw new InvalidOperationException("保险箱已锁定，导入已取消。");
            }

            File.Move(tempPath, destPath);

            var item = new VaultItem
            {
                Id = id,
                OriginalFileName = fileName,
                StoredFileName = destName,
                FileExtension = Path.GetExtension(fileName),
                FileSizeBytes = length,
                MediaType = GetCategoryFromExtension(Path.GetExtension(fileName)),
                ImportedAt = DateTime.Now,
                IsDecoy = decoy
            };

            lock (_sync)
            {
                if (!_isUnlocked || _isDecoyMode != decoy || _unlockEpoch != epoch || _config == null)
                    throw new InvalidOperationException("保险箱状态已改变，导入已取消。");
                var items = CurrentItemListLocked(decoy);
                items.Add(item);
                try
                {
                    SaveConfigOrThrow();
                }
                catch
                {
                    items.Remove(item);
                    throw;
                }
            }
            return item;
        }
        catch
        {
            TryDelete(tempPath);
            if (File.Exists(destPath) && !IsReferencedFile(destName))
            {
                TryDelete(destPath);
            }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task ExportItemAsync(
        VaultItem item,
        string targetFilePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        bool useUniqueName = false)
    {
        long epoch = CaptureUnlockEpoch();
        using var operation = await EnterMutationAsync(cancellationToken, allowPendingRecovery: true, allowPendingRemoval: true);
        string target = Path.GetFullPath(targetFilePath);
        if (IsInsideVault(target)) throw new InvalidOperationException("请选择保险箱目录以外的位置导出文件。");
        string directory = Path.GetDirectoryName(target) ?? throw new ArgumentException("无效的导出路径。", nameof(targetFilePath));
        string tempTarget = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
        byte[] key;
        string sourcePath;
        lock (_sync)
        {
            RequireUnlockEpoch(epoch);
            item = GetCurrentItemLocked(item);
            sourcePath = Path.Combine(_mediaFolder, item.StoredFileName);
            key = CloneCurrentKey();
        }

        try
        {
            ValidateStoredLength(item, CryptoService.ReadHeader(sourcePath).PlainLength);
            Directory.CreateDirectory(directory);
            if (!SafeFileSystem.IsPlainPath(directory))
                throw new InvalidOperationException("导出目录包含链接或无法访问，请选择普通文件夹。");
            progress?.Report(0);
            await Task.Run(() => CryptoService.DecryptToFile(sourcePath, tempTarget, key, progress, cancellationToken), cancellationToken);

            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_isUnlocked || _unlockEpoch != epoch)
                    throw new InvalidOperationException("保险箱状态已改变，导出已取消。");
                if (!SafeFileSystem.IsPlainPath(directory) || IsInsideVault(target))
                    throw new InvalidOperationException("导出位置已改变，请重新选择保险箱以外的位置。");
                if (useUniqueName) PublishUniqueExport(tempTarget, target);
                else File.Move(tempTarget, target, overwrite: true);
            }
            progress?.Report(1.0);
        }
        finally
        {
            if (File.Exists(tempTarget))
            {
                try
                {
                    if (!CryptoService.ShredFile(tempTarget, 1))
                    {
                        TryDelete(tempTarget);
                    }
                }
                catch
                {
                    TryDelete(tempTarget);
                }
            }
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Opens a seekable on-demand decrypting stream for media playback.</summary>
    public Stream OpenDecryptedStream(VaultItem item)
    {
        FileStream input;
        byte[] key;
        lock (_sync)
        {
            EnsureUnlocked();
            item = GetCurrentItemLocked(item);
            // Pin this generation before a rotation can publish new names and clean up the old files.
            input = VaultMediaStream.OpenFile(Path.Combine(_mediaFolder, item.StoredFileName));
            key = (byte[])_currentKey!.Clone();
        }

        try
        {
            var stream = new VaultMediaStream(input, key);
            try { ValidateStoredLength(item, stream.Length); return stream; }
            catch { stream.Dispose(); throw; }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public MemoryStream GetDecryptedMemoryStream(VaultItem item, long maxBytes = 256L * 1024 * 1024)
    {
        using var stream = OpenDecryptedStream(item);
        if (stream.Length > Math.Min(maxBytes, Array.MaxLength))
            throw new InvalidDataException("文件过大，无法整体载入内存。");
        var memory = new MemoryStream((int)stream.Length);
        try
        {
            memory.SetLength(stream.Length);
            stream.ReadExactly(memory.GetBuffer(), 0, (int)stream.Length);
            return memory;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(memory.GetBuffer());
            memory.Dispose();
            throw;
        }
    }

    public Task DeleteItemAsync(VaultItem item) => DeleteItemsAsync([item]);

    internal async Task DeleteItemsAsync(IReadOnlyList<VaultItem> items, IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        if (items.Count == 0) return;
        long epoch = CaptureUnlockEpoch();
        using var operation = await EnterMutationAsync(token, allowPendingRemoval: true);
        RemovalJournal journal;
        List<string> files;
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            RequireUnlockEpoch(epoch);
            foreach (var item in items)
            {
                if (ReferenceEquals(GetCurrentItemLocked(item), item)) continue;
                if (items.Count == 1) return;
                throw new InvalidOperationException("选中项已改变，请重新选择。");
            }
            files = items.Select(item => item.StoredFileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            journal = BeginRemoval(_isDecoyMode ? "decoy-item" : "real-item", files);
        }
        await ExecuteRemovalAsync(journal, progress, files);
    }

    #endregion

    #region Maintenance

    /// <summary>
    /// Removes only aged temporary files when the catalog is trustworthy and idle.
    /// Unreferenced encrypted payloads are retained for recovery, never guessed to be garbage.
    /// </summary>
    public void SweepOrphanedMedia()
    {
        if (!_mutationGate.Wait(0)) return;
        try
        {
            if (!Directory.Exists(_mediaFolder)) return;

            HashSet<string> referenced;
            lock (_sync)
            {
                if (_corrupted || _loadedFromBackup || _config == null || !IsHexHash(_config.RealHashHex)
                    || !IsHexSalt(_config.RealSaltHex) || File.Exists(_rotationJournalPath) || File.Exists(RemovalJournalPath)) return;
                referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_config != null)
                {
                    foreach (var it in _config.Items) if (IsSafeFileName(it.StoredFileName)) referenced.Add(it.StoredFileName);
                    foreach (var it in _config.DecoyItems) if (IsSafeFileName(it.StoredFileName)) referenced.Add(it.StoredFileName);
                }
            }

            int removed = 0;
            foreach (string file in Directory.GetFiles(_mediaFolder))
            {
                string name = Path.GetFileName(file);
                try
                {
                    bool staleTemp = name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                                     || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                    bool staleAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(1);

                    if (referenced.Contains(name)) continue;
                    if (!staleTemp || !staleAge) continue;
                    if (!SafeFileSystem.IsPlainPath(file)) continue;

                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // locked or not deletable; retry next start
                }
            }

            if (removed > 0)
            {
                AppLog.Info($"已清理 {removed} 个孤立/临时保险箱文件");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("清理保险箱残留失败", ex);
        }
        finally { _mutationGate.Release(); }
    }

    #endregion

    #region Rotation (password change)

    private sealed class RotationFilePair
    {
        public string OldName { get; set; } = string.Empty;
        public string NewName { get; set; } = string.Empty;
    }

    private sealed class RotationJournal
    {
        public bool IsDecoy { get; set; }
        public string NewSaltHex { get; set; } = string.Empty;
        public string NewHashHex { get; set; } = string.Empty;
        public int NewIterations { get; set; }
        public List<RotationFilePair> Files { get; set; } = new();
    }

    private async Task RotateAsync(
        VaultConfig config,
        List<VaultItem> items,
        bool decoy,
        byte[] oldKey,
        byte[] newKey,
        byte[] newSalt,
        int newIterations,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        long totalBytes = Math.Max(1, items.Sum(i => Math.Max(0, i.FileSizeBytes)));
        long processed = 0;
        var journal = new RotationJournal
        {
            IsDecoy = decoy,
            NewSaltHex = Convert.ToHexString(newSalt),
            NewHashHex = Convert.ToHexString(CryptoService.HashKey(newKey)),
            NewIterations = newIterations
        };

        var createdFiles = new List<string>();
        var planned = new List<(VaultItem Item, string OldName, string NewName)>();
        bool committed = false;

        try
        {
            foreach (VaultItem item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string oldName = item.StoredFileName;
                string oldPath = Path.Combine(_mediaFolder, oldName);
                if (!File.Exists(oldPath)) throw new FileNotFoundException("保险箱文件缺失，无法更换密码。", oldPath);
                ValidateStoredLength(item, CryptoService.ReadHeader(oldPath).PlainLength);

                string newName = $"{Guid.NewGuid():N}.rotated.svault";
                string newPath = Path.Combine(_mediaFolder, newName);
                if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) || File.Exists(newPath))
                    throw new IOException("密码更换目标必须是新的文件，原文件已保留。");

                long itemBytes = Math.Max(0, item.FileSizeBytes);
                long processedSnapshot = processed;
                var chunkProgress = new Progress<double>(v =>
                {
                    double overall = (double)(processedSnapshot + (long)(itemBytes * v)) / totalBytes;
                    progress?.Report(Math.Clamp(overall * 0.95, 0, 0.95));
                });

                createdFiles.Add(newPath);
                await Task.Run(() => CryptoService.ReEncryptFile(oldPath, newPath, oldKey, newKey, chunkProgress, cancellationToken), cancellationToken);
                planned.Add((item, oldName, newName));
                journal.Files.Add(new RotationFilePair { OldName = oldName, NewName = newName });
                processed += itemBytes;
                progress?.Report(Math.Clamp((double)processed / totalBytes * 0.95, 0, 0.95));
            }

            // Commit: journal first, then config (atomic), then old-file cleanup.
            WriteJournal(journal);

            lock (_sync)
            {
                string previousSalt = decoy ? config.DecoySaltHex : config.RealSaltHex;
                string previousHash = decoy ? config.DecoyHashHex : config.RealHashHex;
                int previousIterations = decoy ? config.DecoyIterations : config.Iterations;

                foreach (var (item, _, newName) in planned)
                {
                    item.StoredFileName = newName;
                }

                if (decoy)
                {
                    config.DecoySaltHex = journal.NewSaltHex;
                    config.DecoyHashHex = journal.NewHashHex;
                    config.DecoyIterations = journal.NewIterations;
                }
                else
                {
                    config.RealSaltHex = journal.NewSaltHex;
                    config.RealHashHex = journal.NewHashHex;
                    config.Iterations = journal.NewIterations;
                }

                try
                {
                    SaveConfigOrThrow();
                    committed = true;
                }
                catch
                {
                    // Roll back all in-memory state so a later save cannot persist a key
                    // that does not match the files on disk.
                    foreach (var (item, oldName, _) in planned) item.StoredFileName = oldName;
                    if (decoy)
                    {
                        config.DecoySaltHex = previousSalt;
                        config.DecoyHashHex = previousHash;
                        config.DecoyIterations = previousIterations;
                    }
                    else
                    {
                        config.RealSaltHex = previousSalt;
                        config.RealHashHex = previousHash;
                        config.Iterations = previousIterations;
                    }
                    throw;
                }
                // The credential and its filenames are one published generation. A lock
                // or a switch to the other space during re-encryption still takes effect.
                if (_isUnlocked && _isDecoyMode == decoy)
                    ReplaceCurrentKey((byte[])newKey.Clone());
            }

            // Keep the fallback catalog consistent before deleting its old payloads.
            // If this copy fails, retain both generations and the recovery journal.
            try { File.Copy(_configPath, _configBackupPath, overwrite: true); }
            catch (Exception ex)
            {
                AppLog.Warn("新密码已提交，配置备份更新失败；两代媒体和恢复记录均已保留", ex);
                return;
            }
            bool cleanupComplete = true;
            foreach (var (_, oldName, _) in planned)
            {
                try
                {
                    string oldPath = Path.Combine(_mediaFolder, oldName);
                    if (File.Exists(oldPath) && !CryptoService.ShredFile(oldPath, 1)) cleanupComplete = false;
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"更换密码后旧文件清理失败: {ex.GetType().Name}");
                    cleanupComplete = false;
                }
            }

            if (cleanupComplete) DeleteJournal();
            progress?.Report(1.0);
        }
        catch
        {
            if (!committed)
            {
                foreach (string file in createdFiles)
                {
                    TryDelete(file);
                }
                DeleteJournal();
            }
            throw;
        }
    }

    private void WriteJournal(RotationJournal journal)
    {
        string json = JsonSerializer.Serialize(journal);
        byte[] plain = Encoding.UTF8.GetBytes(json);
        byte[] cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        string temp = _rotationJournalPath + ".tmp";
        File.WriteAllBytes(temp, cipher);
        File.Move(temp, _rotationJournalPath, overwrite: true);
    }

    private void DeleteJournal()
    {
        try
        {
            if (File.Exists(_rotationJournalPath)) File.Delete(_rotationJournalPath);
            if (File.Exists(_rotationJournalPath + ".tmp")) File.Delete(_rotationJournalPath + ".tmp");
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Crash recovery for an interrupted password change.</summary>
    private void RecoverPendingRotation()
    {
        try
        {
            if (!File.Exists(_rotationJournalPath)) return;
            lock (_sync)
                if (_corrupted || _config == null || !IsHexHash(_config.RealHashHex)
                    || !IsHexSalt(_config.RealSaltHex)) return;

            RotationJournal? journal;
            try
            {
                byte[] cipher = File.ReadAllBytes(_rotationJournalPath);
                byte[] plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                try { journal = JsonSerializer.Deserialize<RotationJournal>(plain); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            catch (Exception ex)
            {
                AppLog.Warn("密码更换日志不可读取，已保留全部媒体与恢复记录", ex);
                return;
            }

            if (journal?.Files == null || !IsHexHash(journal.NewHashHex) || !IsHexSalt(journal.NewSaltHex)) return;

            bool committed;
            lock (_sync)
            {
                committed = _config != null &&
                            (journal.IsDecoy
                                ? string.Equals(_config.DecoyHashHex, journal.NewHashHex, StringComparison.OrdinalIgnoreCase)
                                : string.Equals(_config.RealHashHex, journal.NewHashHex, StringComparison.OrdinalIgnoreCase));
                var references = new HashSet<string>(_config!.Items.Concat(_config.DecoyItems).Select(i => i.StoredFileName),
                    StringComparer.OrdinalIgnoreCase);
                var plannedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in journal.Files)
                {
                    if (!IsSafeFileName(pair.OldName) || !IsSafeFileName(pair.NewName)
                        || !plannedNames.Add(pair.OldName) || !plannedNames.Add(pair.NewName)) return;
                    string keep = committed ? pair.NewName : pair.OldName;
                    string discard = committed ? pair.OldName : pair.NewName;
                    if (!references.Contains(keep) || references.Contains(discard)
                        || !SafeFileSystem.IsPlainPath(Path.Combine(_mediaFolder, keep))) return;
                }
                if (_loadedFromBackup)
                {
                    // A previous-generation backup cannot tell us whether the unreadable
                    // primary committed. Both generations and the journal remain recoverable.
                    if (!committed || !string.Equals(journal.NewSaltHex, journal.IsDecoy ? _config.DecoySaltHex : _config.RealSaltHex,
                            StringComparison.OrdinalIgnoreCase)
                        || journal.NewIterations != (journal.IsDecoy ? _config.DecoyIterations : _config.Iterations)) return;
                    var space = CurrentItemListLocked(journal.IsDecoy).Select(i => i.StoredFileName);
                    if (!new HashSet<string>(space, StringComparer.OrdinalIgnoreCase).SetEquals(journal.Files.Select(p => p.NewName))) return;
                    ValidateBackupMedia(journal.Files.Select(p => p.OldName), Array.Empty<string>());
                    SaveConfigOrThrow();
                }
            }

            if (committed) File.Copy(_configPath, _configBackupPath, overwrite: true);

            foreach (var pair in journal.Files)
            {
                string discard = Path.Combine(_mediaFolder, committed ? pair.OldName : pair.NewName);
                if (IsDefinitelyMissing(discard)) continue;
                if (!SafeFileSystem.IsPlainPath(discard)) return;
                File.Delete(discard); // A failure retains the journal for the next recovery attempt.
            }

            DeleteJournal();
            AppLog.Info(committed ? "已完成中断的密码更换清理" : "已回滚中断的密码更换");
        }
        catch (Exception ex)
        {
            AppLog.Error("恢复中断的密码更换失败", ex);
        }
    }

    #endregion

    #region Config persistence

    // Only called during construction or with the mutation gate held. A reset may
    // supersede a rotation; two ordinary journals do not establish an order.
    private void RecoverPendingOperations()
    {
        try
        {
            // A sharing/access error can hide a newer, healthy primary. Retry the
            // read while locked before writing a catalog or cleaning either generation.
            lock (_sync)
                if (_primaryReadDeferred && !_isUnlocked) LoadConfig();
            if (!IsDefinitelyMissing(RemovalJournalPath))
            {
                RecoverPendingRemoval();
                return;
            }
            if (_primaryReadDeferred) return;
            if (!IsDefinitelyMissing(_rotationJournalPath))
            {
                RecoverPendingRotation();
                return;
            }
            lock (_sync)
                if (_loadedFromBackup && !_corrupted) SaveConfigOrThrow();
        }
        catch (Exception ex)
        {
            AppLog.Warn("保险箱索引尚未恢复，已保留备份和媒体", ex);
        }
    }

    // A stale backup can omit imports or still reference unrelated deleted files.
    // Reconcile it with a journal only when the two account for the payloads on disk.
    private void ValidateBackupMedia(IEnumerable<string> extraFiles, IEnumerable<string> mayBeMissing)
    {
        var expected = new HashSet<string>(extraFiles, StringComparer.OrdinalIgnoreCase);
        var optional = new HashSet<string>(mayBeMissing, StringComparer.OrdinalIgnoreCase);
        foreach (var item in _config!.Items.Concat(_config.DecoyItems))
        {
            if (!expected.Add(item.StoredFileName))
                throw new InvalidDataException("备份索引中存在冲突的媒体记录。");
            string path = Path.Combine(_mediaFolder, item.StoredFileName);
            if (optional.Contains(item.StoredFileName) && IsDefinitelyMissing(path)) continue;
            if (!SafeFileSystem.IsPlainPath(path)) throw new InvalidDataException("备份索引引用的媒体不可用。");
            ValidateStoredLength(item, CryptoService.ReadHeader(path).PlainLength);
        }
        if (Directory.EnumerateFiles(_mediaFolder, "*.svault").Any(path => !expected.Contains(Path.GetFileName(path))))
            throw new InvalidDataException("媒体目录中存在备份索引和恢复记录未包含的文件。");
    }

    private void LoadConfig()
    {
        lock (_sync)
        {
            _loadedFromBackup = false;
            _primaryReadDeferred = false;
            try
            {
                if (IsDefinitelyMissing(_configPath) && IsDefinitelyMissing(_configBackupPath))
                {
                    _config = new VaultConfig();
                    _corrupted = false;
                    return;
                }

                VaultConfig? loaded;
                try { loaded = TryLoadConfigFile(_configPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _primaryReadDeferred = true;
                    loaded = null;
                    AppLog.Warn("主索引暂时无法访问，保留原文件", ex);
                }
                if (loaded == null)
                {
                    AppLog.Warn("保险箱配置无法读取，尝试备份");
                    loaded = TryLoadConfigFile(_configBackupPath);
                    if (loaded != null)
                    {
                        _loadedFromBackup = true;
                        AppLog.Info("已读取保险箱备份索引");
                    }
                }

                if (loaded == null)
                {
                    _config = new VaultConfig();
                    _corrupted = true;
                    _loadError = "保险箱配置文件损坏或无法解密。原始文件已保留，可在设置中重置保险箱。";
                    AppLog.Error("保险箱配置损坏", null);
                    return;
                }

                SanitizeConfig(loaded);
                if (!IsHexHash(loaded.RealHashHex) || !IsHexSalt(loaded.RealSaltHex))
                {
                    // The file decrypted but does not describe a usable vault: treat as corrupted,
                    // never silently offer first-run setup (which would overwrite the file).
                    _config = loaded;
                    _corrupted = true;
                    _loadError = "保险箱配置缺少有效的密码校验信息。";
                    AppLog.Error("保险箱配置不完整", null);
                    return;
                }

                _config = loaded;
                _corrupted = false;
                _loadError = string.Empty;
            }
            catch (Exception ex)
            {
                _config = new VaultConfig();
                _corrupted = true;
                _loadError = "读取保险箱配置时发生错误。";
                AppLog.Error("读取保险箱配置失败", ex);
            }
        }
    }

    private VaultConfig? TryLoadConfigFile(string path)
    {
        try
        {
            byte[] encrypted = File.ReadAllBytes(path);
            if (encrypted.Length == 0) return null;
            byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<VaultConfig>(decrypted); }
            finally { CryptographicOperations.ZeroMemory(decrypted); }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or CryptographicException or JsonException)
        {
            AppLog.Warn($"读取保险箱配置文件失败: {ex.GetType().Name}");
            return null;
        }
    }

    private static void SanitizeConfig(VaultConfig config)
    {
        config.Items ??= new List<VaultItem>();
        config.DecoyItems ??= new List<VaultItem>();

        config.Items = config.Items.Where(i => i != null && IsSafeFileName(i.StoredFileName)).ToList();
        config.DecoyItems = config.DecoyItems.Where(i => i != null && IsSafeFileName(i.StoredFileName)).ToList();

        // De-duplicate by id and file name: a duplicated entry would otherwise make later
        // rotations/delete operations ambiguous or point two records at one file.
        Deduplicate(config.Items, isDecoy: false);
        Deduplicate(config.DecoyItems, isDecoy: true);

        config.Iterations = Math.Clamp(config.Iterations, 10_000, 10_000_000);
        config.DecoyIterations = config.DecoyIterations <= 0
            ? CryptoService.DefaultIterations
            : Math.Clamp(config.DecoyIterations, 10_000, 10_000_000);
    }

    private static void Deduplicate(List<VaultItem> items, bool isDecoy)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<VaultItem>(items.Count);

        foreach (VaultItem item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");
            if (!seenIds.Add(item.Id)) continue;
            if (!seenFiles.Add(item.StoredFileName)) continue;

            item.IsDecoy = isDecoy;
            item.OriginalFileName ??= "未命名";
            item.FileExtension ??= string.Empty;
            if (item.FileSizeBytes < 0) item.FileSizeBytes = 0;
            unique.Add(item);
        }

        items.Clear();
        items.AddRange(unique);
    }

    private void SaveConfigOrThrow(VaultConfig? candidate = null)
    {
        lock (_sync)
        {
            candidate ??= _config ?? throw new InvalidOperationException("保险箱配置未加载。");
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(candidate);
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(plain); }

            string temp = _configPath + ".tmp";
            File.WriteAllBytes(temp, encrypted);

            if (!_loadedFromBackup && File.Exists(_configPath))
            {
                try
                {
                    File.Replace(temp, _configPath, _configBackupPath, ignoreMetadataErrors: true);
                }
                catch (Exception)
                {
                    File.Move(temp, _configPath, overwrite: true);
                }
            }
            else
            {
                // During fallback the existing primary is unreadable. Keep the
                // known-good backup intact while publishing the recovered catalog.
                File.Move(temp, _configPath, overwrite: true);
            }
            _loadedFromBackup = false;
        }
    }

    private void LoadConfigWithLegacyMigration()
    {
        string? migrationError = _legacyMigrationSource == null ? null : MigrateOldVaultIfPresent(_legacyMigrationSource);
        LoadConfig();
        if (migrationError != null && !HasVaultCore)
        {
            _corrupted = true;
            _loadError = migrationError;
        }
    }

    private string? MigrateOldVaultIfPresent(string oldStore)
    {
        if (AppPaths.IsLegacyImportDisabled(_vaultRoot)) return null;
        string oldConfig = Path.Combine(oldStore, "vault_config.dat");
        if (!File.Exists(oldConfig)) return null;
        string? staging = null;
        var moved = new List<string>();
        bool committed = false;
        const string retained = "旧版保险箱尚未迁移完成，原目录与文件已保留。请检查文件占用或目录冲突后重新启动。";
        bool HasTargetMetadata() => new[] { _configPath, _configBackupPath, _rotationJournalPath, RemovalJournalPath }
            .Any(path => !IsDefinitelyMissing(path));
        try
        {
            // An established destination is authoritative. Never merge another vault's
            // ciphertext into it, and never remove the untouched legacy source.
            if (HasTargetMetadata()) return retained;
            if (!SafeFileSystem.IsPlainPath(_vaultRoot) || !SafeFileSystem.IsPlainPath(_mediaFolder))
                throw new IOException("迁移目标包含目录链接，原文件已保留。");
            if (Directory.EnumerateFileSystemEntries(_mediaFolder).Any())
                throw new IOException("目标媒体目录非空，不能合并旧保险箱。");
            if (!SafeFileSystem.IsPlainPath(oldConfig)) throw new IOException("旧版索引路径不安全。");
            using var catalogInput = new FileStream(oldConfig, FileMode.Open, FileAccess.Read, FileShare.Read);
            var migrated = JsonSerializer.Deserialize<VaultConfig>(catalogInput)
                ?? throw new InvalidDataException("旧版索引无效。");
            if (!IsHexHash(migrated.RealHashHex) || !IsHexSalt(migrated.RealSaltHex) ||
                migrated.Items == null || migrated.DecoyItems == null)
                throw new InvalidDataException("旧版索引缺少有效的密码校验信息。");
            int originalCount = migrated.Items.Count + migrated.DecoyItems.Count;
            SanitizeConfig(migrated);
            var items = migrated.Items.Concat(migrated.DecoyItems).ToArray();
            if (items.Length != originalCount || items.Select(item => item.StoredFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length)
                throw new InvalidDataException("旧版索引包含重复或无效媒体记录。");
            if (migrated.DecoyHashHex.Length != 0 && (!IsHexHash(migrated.DecoyHashHex) || !IsHexSalt(migrated.DecoySaltHex)))
                throw new InvalidDataException("旧版诱饵索引无效。");

            staging = Path.Combine(_vaultRoot, ".legacy-migration-" + Guid.NewGuid().ToString("N"));
            string stagedMedia = Path.Combine(staging, "MediaVault");
            Directory.CreateDirectory(stagedMedia);
            foreach (var item in items)
            {
                string source = Path.Combine(oldStore, "EncryptedMedia", item.StoredFileName);
                if (!SafeFileSystem.IsPlainPath(source)) throw new IOException("旧版媒体无法安全读取。");
                string target = Path.Combine(stagedMedia, item.StoredFileName);
                File.Copy(source, target, overwrite: false);
                File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                ValidateStoredLength(item, CryptoService.ReadHeader(target).PlainLength);
            }
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(migrated);
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            string stagedCatalog = Path.Combine(staging, "system_catalog.dat");
            using (var output = new FileStream(stagedCatalog, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(encrypted);
                output.Flush(true);
            }
            if (HasTargetMetadata() || Directory.EnumerateFileSystemEntries(_mediaFolder).Any())
                throw new IOException("迁移目标已改变，原文件已保留。");
            foreach (var item in items)
            {
                string target = Path.Combine(_mediaFolder, item.StoredFileName);
                File.Move(Path.Combine(stagedMedia, item.StoredFileName), target);
                moved.Add(target);
            }
            // Commit last and never overwrite a destination that appeared meanwhile.
            File.Move(stagedCatalog, _configPath);
            committed = true;
            AppLog.Info("旧版保险箱已复制并验证，原目录保留以便回滚");
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("旧版保险箱迁移未完成，原始目录已保留", ex);
            return retained;
        }
        finally
        {
            if (!committed) foreach (string file in moved) TryDelete(file);
            if (staging != null)
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
                catch (Exception ex) { AppLog.Warn("旧版迁移临时目录保留: " + ex.GetType().Name); }
            }
        }
    }
    #endregion

    #region Helpers

    private bool HasVaultCore => _config != null && (IsHexHash(_config.RealHashHex) || _corrupted);

    private void EnsureNewVaultTarget()
    {
        if (HasVaultCore) throw new InvalidOperationException("保险箱已存在，不能重复初始化。");
        EnsureMediaDirectory();
        if (Directory.EnumerateFileSystemEntries(_mediaFolder).Any())
            throw new InvalidOperationException("现有媒体目录含有待恢复的文件，未创建新保险箱。请先恢复匹配的备份或保留原件后检查目录。");
    }

    private void EnsureMediaDirectory()
    {
        if (!SafeFileSystem.IsPlainPath(_vaultRoot)) throw new IOException("保险箱目录不可安全访问。");
        Directory.CreateDirectory(_mediaFolder);
        if (!SafeFileSystem.IsPlainPath(_mediaFolder)) throw new IOException("媒体目录不可安全访问。");
    }

    private void ReplaceCurrentKey(byte[] newKey)
    {
        if (_currentKey != null)
        {
            CryptographicOperations.ZeroMemory(_currentKey);
        }
        _currentKey = newKey;
    }

    private byte[] CloneCurrentKey()
    {
        lock (_sync)
        {
            if (!_isUnlocked || _currentKey == null) throw new InvalidOperationException("保险箱未解锁。");
            return (byte[])_currentKey.Clone();
        }
    }

    private void EnsureUnlocked(bool requireReal = false)
    {
        lock (_sync)
        {
            if (!_isUnlocked || _currentKey == null) throw new InvalidOperationException("保险箱未解锁。");
            if (requireReal && _isDecoyMode) throw new InvalidOperationException("该操作只能在真实保险箱空间中执行。");
        }
    }

    private VaultItem GetCurrentItemLocked(VaultItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var entry = CurrentItemListLocked(_isDecoyMode).FirstOrDefault(i => i.Id == item.Id)
            ?? throw new InvalidOperationException("该媒体不属于当前保险箱空间。");
        if (!IsSafeFileName(entry.StoredFileName)) throw new InvalidOperationException("媒体记录无效。");
        return entry;
    }

    private bool IsCurrentUnlockSession(long epoch, bool decoy)
    {
        lock (_sync) return _isUnlocked && _unlockEpoch == epoch && _isDecoyMode == decoy;
    }

    // Call under _sync where the operation claims its key, item or durable deletion intent.
    private void RequireUnlockEpoch(long epoch)
    {
        EnsureUnlocked();
        if (_unlockEpoch != epoch) throw new InvalidOperationException("保险箱已重新锁定，请在当前会话重试。");
    }

    private static void ValidateStoredLength(VaultItem item, long actualLength)
    {
        if (actualLength != item.FileSizeBytes)
            throw new InvalidDataException("媒体长度与加密索引不一致，文件可能已损坏。原文件已保留。");
    }

    private bool IsReferencedFile(string storedName)
    {
        lock (_sync)
        {
            if (_config == null) return false;
            return _config.Items.Any(i => string.Equals(i.StoredFileName, storedName, StringComparison.OrdinalIgnoreCase))
                || _config.DecoyItems.Any(i => string.Equals(i.StoredFileName, storedName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private List<VaultItem> CurrentItemListLocked(bool decoy)
    {
        if (_config == null) throw new InvalidOperationException("保险箱配置未加载。");
        return decoy ? _config.DecoyItems : _config.Items;
    }

    private List<VaultItem> SnapshotCurrentItems(out VaultConfig config, bool decoy)
    {
        lock (_sync)
        {
            config = _config ?? throw new InvalidOperationException("保险箱配置未加载。");
            return (decoy ? config.DecoyItems : config.Items)
                .Where(i => !string.IsNullOrEmpty(i.StoredFileName))
                .ToList();
        }
    }

    private bool IsInsideVault(string fullPath)
    {
        string vaultFull = SafeFileSystem.ResolvePath(_vaultRoot);
        string candidate = SafeFileSystem.ResolvePath(fullPath);
        return candidate.Equals(vaultFull, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(vaultFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Contains("..", StringComparison.Ordinal)) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal);
    }

    private static bool IsHexSalt(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length != SaltSizeHexLength) return false;
        try
        {
            return Convert.FromHexString(hex).Length == CryptoService.SaltSize;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsHexHash(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length != HashSizeHexLength) return false;
        try
        {
            return Convert.FromHexString(hex).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private const int SaltSizeHexLength = CryptoService.SaltSize * 2;
    private const int HashSizeHexLength = 32 * 2;

    private static void ValidateNewPassword(string password, string paramName)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinPasswordLength)
            throw new ArgumentException($"密码至少需要 {MinPasswordLength} 个字符。", paramName);
        if (password.Length > 1024) throw new ArgumentException("密码过长。", paramName);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    private static string GetCategoryFromExtension(string ext)
    {
        return ext.ToLowerInvariant() switch
        {
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".flv" or ".m4v" or ".ts" => "Video",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".heic" or ".avif" or ".tif" or ".tiff" or ".ico" => "Image",
            ".mp3" or ".flac" or ".wav" or ".m4a" or ".aac" or ".ogg" => "Audio",
            ".pdf" or ".txt" or ".doc" or ".docx" or ".zip" or ".7z" or ".md" => "Document",
            _ => "Other"
        };
    }

    #endregion
}
