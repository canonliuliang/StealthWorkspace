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

public enum VaultUnlockStatus
{
    Success,
    WrongPassword,
    LockedOut,
    NoVault,
    Corrupted,
    Error
}

public sealed class VaultUnlockResult
{
    public VaultUnlockStatus Status { get; init; }
    public bool IsDecoy { get; init; }
    public int RetryAfterSeconds { get; init; }
    public string Message { get; init; } = string.Empty;
    internal byte[]? Key { get; set; }

    internal void ForgetKey()
    {
        if (Key != null)
        {
            CryptographicOperations.ZeroMemory(Key);
            Key = null;
        }
    }
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

    private const int MaxFailedAttemptsBeforeDelay = 5;
    private const int LockoutSecondsShort = 30;
    private const int LockoutSecondsLong = 300;
    private const int RotationFileSuffix = 2;
    private const int MinPasswordLength = 6;

    private readonly string _vaultRoot;
    private readonly string _mediaFolder;
    private readonly string _configPath;
    private readonly string _configBackupPath;
    private readonly string _rotationJournalPath;
    private readonly object _sync = new();

    private VaultConfig? _config;
    private byte[]? _currentKey;
    private bool _isUnlocked;
    private bool _isDecoyMode;
    private bool _corrupted;
    private string _loadError = string.Empty;

    private int _failedAttempts;
    private DateTime _lockoutUntilUtc = DateTime.MinValue;

    public VaultService() : this(null)
    {
    }

    /// <summary>Test/diagnostic constructor allowing an isolated data root.</summary>
    public VaultService(string? dataRootOverride)
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

        MigrateOldVaultIfPresent();
        LoadConfig();
        RecoverPendingRotation();
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
                return _corrupted || (_config != null && IsHexHash(_config.RealHashHex));
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
        LoadConfig();
        RecoverPendingRotation();
    }

    public ObservableCollection<VaultItem> VisibleItems { get; } = new();

    public event Action? Locked;
    public event Action? Unlocked;

    #endregion

    #region Unlock / Lock

    /// <summary>
    /// Verifies a password off the UI thread and returns an opaque result. The caller
    /// must dispose the key by calling <see cref="ApplyUnlock"/> (or the result's
    /// internal key is zeroed when the result is finalized). Never mutates UI state.
    /// </summary>
    public VaultUnlockResult VerifyPassword(string password)
    {
        try
        {
            lock (_sync)
            {
                if (_corrupted)
                {
                    return new VaultUnlockResult
                    {
                        Status = VaultUnlockStatus.Corrupted,
                        Message = "保险箱配置损坏，无法验证密码。"
                    };
                }

                if (_config == null || !IsHexHash(_config.RealHashHex))
                {
                    return new VaultUnlockResult { Status = VaultUnlockStatus.NoVault, Message = "尚未创建保险箱。" };
                }

                DateTime now = DateTime.UtcNow;
                if (now < _lockoutUntilUtc)
                {
                    return new VaultUnlockResult
                    {
                        Status = VaultUnlockStatus.LockedOut,
                        RetryAfterSeconds = Math.Max(1, (int)Math.Ceiling((_lockoutUntilUtc - now).TotalSeconds))
                    };
                }

                if (string.IsNullOrEmpty(password))
                {
                    return new VaultUnlockResult { Status = VaultUnlockStatus.WrongPassword, Message = "请输入密码。" };
                }

                byte[]? key = null;
                try
                {
                    key = CryptoService.DeriveKey(password, Convert.FromHexString(_config.RealSaltHex), _config.Iterations);
                    if (CryptoService.VerifyKeyHash(key, _config.RealHashHex))
                    {
                        ResetThrottle();
                        // Ownership of the key is transferred to the result; the finally
                        // block must not zero it (that would silently unlock with a key of
                        // all zeroes and make every decryption fail).
                        var success = new VaultUnlockResult
                        {
                            Status = VaultUnlockStatus.Success,
                            IsDecoy = false,
                            Key = key
                        };
                        key = null;
                        return success;
                    }
                    CryptographicOperations.ZeroMemory(key);
                    key = null;

                    if (IsHexHash(_config.DecoyHashHex) && IsHexSalt(_config.DecoySaltHex))
                    {
                        key = CryptoService.DeriveKey(password, Convert.FromHexString(_config.DecoySaltHex),
                            _config.DecoyIterations > 0 ? _config.DecoyIterations : CryptoService.DefaultIterations);
                        if (CryptoService.VerifyKeyHash(key, _config.DecoyHashHex))
                        {
                            ResetThrottle();
                            var decoySuccess = new VaultUnlockResult
                            {
                                Status = VaultUnlockStatus.Success,
                                IsDecoy = true,
                                Key = key
                            };
                            key = null;
                            return decoySuccess;
                        }
                    }
                }
                catch (Exception ex) when (ex is FormatException or CryptographicException)
                {
                    AppLog.Error("保险箱密码验证异常", ex);
                    return new VaultUnlockResult { Status = VaultUnlockStatus.Error, Message = "保险箱数据不可用。" };
                }
                finally
                {
                    if (key != null) CryptographicOperations.ZeroMemory(key);
                }

                return RegisterFailure(now);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("保险箱密码验证失败", ex);
            return new VaultUnlockResult { Status = VaultUnlockStatus.Error, Message = "发生内部错误。" };
        }
    }

    /// <summary>Applies a successful verification on the UI thread and loads the visible item list.</summary>
    public bool ApplyUnlock(VaultUnlockResult result)
    {
        if (result == null || result.Status != VaultUnlockStatus.Success || result.Key == null)
        {
            result?.ForgetKey();
            return false;
        }

        try
        {
            lock (_sync)
            {
                if (_currentKey != null)
                {
                    CryptographicOperations.ZeroMemory(_currentKey);
                }

                _currentKey = result.Key;
                result.Key = null;
                _isUnlocked = true;
                _isDecoyMode = result.IsDecoy;
                RefreshVisibleItemsLocked();
            }

            Unlocked?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("应用解锁状态失败", ex);
            result.ForgetKey();
            return false;
        }
    }

    public void Lock()
    {
        bool wasUnlocked;
        lock (_sync)
        {
            wasUnlocked = _isUnlocked;
            _isUnlocked = false;
            _isDecoyMode = false;
            if (_currentKey != null)
            {
                CryptographicOperations.ZeroMemory(_currentKey);
                _currentKey = null;
            }
            VisibleItems.Clear();
        }

        if (wasUnlocked)
        {
            Locked?.Invoke();
        }
    }

    private VaultUnlockResult RegisterFailure(DateTime now)
    {
        _failedAttempts++;
        int retryAfter = 0;
        if (_failedAttempts >= MaxFailedAttemptsBeforeDelay)
        {
            int seconds = _failedAttempts >= 10 ? LockoutSecondsLong : LockoutSecondsShort;
            _lockoutUntilUtc = now.AddSeconds(seconds);
            retryAfter = seconds;
            AppLog.Warn($"保险箱连续验证失败 {_failedAttempts} 次，已临时锁定 {seconds} 秒");
        }

        return new VaultUnlockResult
        {
            Status = retryAfter > 0 ? VaultUnlockStatus.LockedOut : VaultUnlockStatus.WrongPassword,
            RetryAfterSeconds = retryAfter,
            Message = retryAfter > 0
                ? $"尝试次数过多，请在 {retryAfter} 秒后重试。"
                : "密码错误，请重试。"
        };
    }

    private void ResetThrottle()
    {
        _failedAttempts = 0;
        _lockoutUntilUtc = DateTime.MinValue;
    }

    #endregion

    #region Vault creation / password management

    public async Task InitializeVaultAsync(string masterPassword, string? decoyPassword = null, IProgress<double>? progress = null)
    {
        ValidateNewPassword(masterPassword, nameof(masterPassword));
        if (!string.IsNullOrEmpty(decoyPassword))
        {
            ValidateNewPassword(decoyPassword, nameof(decoyPassword));
            if (string.Equals(masterPassword, decoyPassword, StringComparison.Ordinal))
                throw new ArgumentException("主密码与诱饵密码不能相同。");
        }

        lock (_sync)
        {
            if (HasVaultCore) throw new InvalidOperationException("保险箱已存在，不能重复初始化。");
        }

        progress?.Report(0.05);
        (byte[] realSalt, byte[] realKey) = await Task.Run(() =>
        {
            byte[] salt = CryptoService.GenerateRandomBytes(CryptoService.SaltSize);
            byte[] key = CryptoService.DeriveKey(masterPassword, salt, CryptoService.DefaultIterations);
            return (salt, key);
        });

        progress?.Report(0.6);

        byte[]? decoySalt = null;
        byte[]? decoyKey = null;
        if (!string.IsNullOrEmpty(decoyPassword))
        {
            (decoySalt, decoyKey) = await Task.Run(() =>
            {
                byte[] salt = CryptoService.GenerateRandomBytes(CryptoService.SaltSize);
                byte[] key = CryptoService.DeriveKey(decoyPassword!, salt, CryptoService.DefaultIterations);
                return (salt, key);
            });
        }

        progress?.Report(0.9);

        try
        {
            lock (_sync)
            {
                if (HasVaultCore) throw new InvalidOperationException("保险箱已存在，不能重复初始化。");

                _config = new VaultConfig
                {
                    Version = 2,
                    RealSaltHex = Convert.ToHexString(realSalt),
                    RealHashHex = Convert.ToHexString(CryptoService.HashKey(realKey)),
                    Iterations = CryptoService.DefaultIterations,
                    Items = new List<VaultItem>()
                };

                if (decoySalt != null && decoyKey != null)
                {
                    _config.DecoySaltHex = Convert.ToHexString(decoySalt);
                    _config.DecoyHashHex = Convert.ToHexString(CryptoService.HashKey(decoyKey));
                    _config.DecoyIterations = CryptoService.DefaultIterations;
                    _config.DecoyItems = new List<VaultItem>();
                }

                SaveConfigOrThrow();
                _corrupted = false;
                _loadError = string.Empty;

                ReplaceCurrentKey(realKey);
                realKey = Array.Empty<byte>();
                _isUnlocked = true;
                _isDecoyMode = false;
                RefreshVisibleItemsLocked();
            }

            Unlocked?.Invoke();
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
        ValidateNewPassword(newPassword, nameof(newPassword));
        EnsureUnlocked(requireReal: true);
        var items = SnapshotCurrentItems(out VaultConfig config, decoy: false);
        byte[] oldKey = CloneCurrentKey();

        byte[] newSalt = CryptoService.GenerateRandomBytes(CryptoService.SaltSize);
        byte[]? newKey = null;
        try
        {
            progress?.Report(0.02);
            newKey = await Task.Run(() => CryptoService.DeriveKey(newPassword, newSalt, CryptoService.DefaultIterations), cancellationToken);
            await RotateAsync(config, items, decoy: false, oldKey, newKey, newSalt,
                CryptoService.DefaultIterations, progress, cancellationToken);

            byte[] appliedKey = newKey;
            newKey = null;
            bool stillUnlocked;
            lock (_sync)
            {
                stillUnlocked = _isUnlocked && !_isDecoyMode;
                if (stillUnlocked)
                {
                    ReplaceCurrentKey(appliedKey);
                    RefreshVisibleItemsLocked();
                }
            }

            if (!stillUnlocked)
            {
                // The workspace was locked (boss key / idle) while the rotation ran.
                // The config is already committed; keep the new key out of memory.
                CryptographicOperations.ZeroMemory(appliedKey);
            }

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

                if (decoyItems.Count > 0)
                {
                    await RotateAsync(config, decoyItems, decoy: true, oldDecoyKey, newKey, newSalt,
                        CryptoService.DefaultIterations, progress, cancellationToken);
                }
                else
                {
                    lock (_sync)
                    {
                        config.DecoySaltHex = Convert.ToHexString(newSalt);
                        config.DecoyHashHex = Convert.ToHexString(CryptoService.HashKey(newKey));
                        config.DecoyIterations = CryptoService.DefaultIterations;
                        SaveConfigOrThrow();
                    }
                }
            }
            else
            {
                lock (_sync)
                {
                    config.DecoySaltHex = Convert.ToHexString(newSalt);
                    config.DecoyHashHex = Convert.ToHexString(CryptoService.HashKey(newKey));
                    config.DecoyIterations = CryptoService.DefaultIterations;
                    config.DecoyItems ??= new List<VaultItem>();
                    SaveConfigOrThrow();
                }
            }

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
        EnsureUnlocked(requireReal: true);

        List<string> toDelete = new();
        lock (_sync)
        {
            if (_config == null) return;
            toDelete = _config.DecoyItems.Select(i => i.StoredFileName).Where(IsSafeFileName).ToList();
        }

        await Task.Run(() =>
        {
            foreach (string name in toDelete)
            {
                try { CryptoService.ShredFile(Path.Combine(_mediaFolder, name), 1); } catch { }
            }
        });

        lock (_sync)
        {
            if (_config == null) return;
            _config.DecoyItems.Clear();
            _config.DecoySaltHex = string.Empty;
            _config.DecoyHashHex = string.Empty;
            _config.DecoyIterations = 0;
            SaveConfigOrThrow();
        }
    }

    public async Task ResetVaultAsync()
    {
        Lock();

        await Task.Run(() =>
        {
            try
            {
                if (File.Exists(_configPath)) File.Delete(_configPath);
                if (File.Exists(_configBackupPath)) File.Delete(_configBackupPath);
                if (File.Exists(_rotationJournalPath)) File.Delete(_rotationJournalPath);
                if (Directory.Exists(_mediaFolder))
                {
                    foreach (string file in Directory.GetFiles(_mediaFolder))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("重置保险箱失败", ex);
            }
        });

        lock (_sync)
        {
            _config = new VaultConfig();
            _corrupted = false;
            _loadError = string.Empty;
            VisibleItems.Clear();
        }
    }

    #endregion

    #region Import / Export / Delete

    public async Task<VaultItem> ImportFileAsync(
        string sourcePath,
        bool shredOriginal,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        Action<bool>? reportOriginalRemoval = null)
    {
        EnsureUnlocked();
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("文件路径为空。", nameof(sourcePath));

        string fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath)) throw new FileNotFoundException("源文件不存在。", fullSourcePath);
        if (IsInsideVault(fullSourcePath)) throw new InvalidOperationException("不能把保险箱自身的文件再次导入。");

        bool decoy = IsDecoyMode;
        byte[] key = CloneCurrentKey();

        string id = Guid.NewGuid().ToString("N");
        string destName = id + ".svault";
        string destPath = Path.Combine(_mediaFolder, destName);
        string tempPath = destPath + ".part";

        try
        {
            Directory.CreateDirectory(_mediaFolder);
            var fileInfo = new FileInfo(fullSourcePath);
            long length = fileInfo.Length;

            progress?.Report(0);
            await Task.Run(() => CryptoService.EncryptFile(fullSourcePath, tempPath, key, progress, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Re-check state: the user might have locked the vault while encrypting.
            if (!IsUnlocked || IsDecoyMode != decoy)
            {
                TryDelete(tempPath);
                throw new InvalidOperationException("保险箱已锁定，导入已取消。");
            }

            File.Move(tempPath, destPath);

            var item = new VaultItem
            {
                Id = id,
                OriginalFileName = fileInfo.Name,
                StoredFileName = destName,
                FileExtension = fileInfo.Extension,
                FileSizeBytes = length,
                MediaType = GetCategoryFromExtension(fileInfo.Extension),
                ImportedAt = DateTime.Now,
                IsDecoy = decoy
            };

            bool committed = false;
            lock (_sync)
            {
                try
                {
                    if (!_isUnlocked || _isDecoyMode != decoy || _config == null)
                        throw new InvalidOperationException("保险箱状态已改变，导入已取消。");

                    CurrentItemListLocked(decoy).Add(item);
                    SaveConfigOrThrow();
                    committed = true;
                }
                catch
                {
                    // roll back the in-memory list entry
                    CurrentItemListLocked(decoy).Remove(item);
                    throw;
                }
            }

            if (!committed)
            {
                TryDelete(destPath);
                throw new InvalidOperationException("保存保险箱配置失败，导入已取消。");
            }

            if (IsUnlocked && IsDecoyMode == decoy)
            {
                VisibleItems.Add(item);
            }

            if (shredOriginal)
            {
                bool removed = !cancellationToken.IsCancellationRequested && IsUnlocked && IsDecoyMode == decoy
                    && await Task.Run(() => CryptoService.ShredFile(fullSourcePath));
                reportOriginalRemoval?.Invoke(removed);
            }

            progress?.Report(1.0);
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
        CancellationToken cancellationToken = default)
    {
        EnsureUnlocked();
        ValidateItem(item);

        string sourcePath = Path.Combine(_mediaFolder, item.StoredFileName);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("保险箱内找不到该媒体文件。", sourcePath);

        string target = Path.GetFullPath(targetFilePath);
        string directory = Path.GetDirectoryName(target) ?? throw new ArgumentException("无效的导出路径。", nameof(targetFilePath));
        Directory.CreateDirectory(directory);

        bool decoy = IsDecoyMode;
        string tempTarget = target + ".part";
        byte[] key = CloneCurrentKey();

        try
        {
            progress?.Report(0);
            await Task.Run(() => CryptoService.DecryptToFile(sourcePath, tempTarget, key, progress, cancellationToken), cancellationToken);

            if (!IsUnlocked || IsDecoyMode != decoy)
            {
                TryDelete(tempTarget);
                throw new InvalidOperationException("保险箱已锁定，导出已取消。");
            }

            File.Move(tempTarget, target, overwrite: true);
            progress?.Report(1.0);
        }
        finally
        {
            TryDelete(tempTarget);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Opens a seekable on-demand decrypting stream for media playback.</summary>
    public Stream OpenDecryptedStream(VaultItem item)
    {
        EnsureUnlocked();
        ValidateItem(item);

        string path = Path.Combine(_mediaFolder, item.StoredFileName);
        if (!File.Exists(path)) throw new FileNotFoundException("保险箱内找不到该媒体文件。", path);

        byte[] key;
        lock (_sync)
        {
            if (_currentKey == null) throw new InvalidOperationException("保险箱未解锁。");
            key = (byte[])_currentKey.Clone();
        }

        try
        {
            return new VaultMediaStream(path, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public MemoryStream GetDecryptedMemoryStream(VaultItem item, long maxBytes = 256L * 1024 * 1024)
    {
        EnsureUnlocked();
        ValidateItem(item);

        string path = Path.Combine(_mediaFolder, item.StoredFileName);
        if (!File.Exists(path)) throw new FileNotFoundException("保险箱内找不到该媒体文件。", path);

        byte[] key;
        lock (_sync)
        {
            if (_currentKey == null) throw new InvalidOperationException("保险箱未解锁。");
            key = (byte[])_currentKey.Clone();
        }

        try
        {
            return CryptoService.DecryptToMemoryStream(path, key, maxBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task DeleteItemAsync(VaultItem item)
    {
        EnsureUnlocked();
        ValidateItem(item);

        bool decoy = IsDecoyMode;
        string storedName = item.StoredFileName;

        lock (_sync)
        {
            var list = CurrentItemListLocked(decoy);
            if (!list.Remove(item)) return;
            try
            {
                SaveConfigOrThrow();
            }
            catch
            {
                list.Add(item);
                throw;
            }
        }

        VisibleItems.Remove(item);
        await Task.Run(() => CryptoService.ShredFile(Path.Combine(_mediaFolder, storedName), 1));
    }

    #endregion

    #region Maintenance

    /// <summary>
    /// Removes leftovers from interrupted imports/rotations and encrypted files that are
    /// no longer referenced by either vault. Called at startup, off the UI thread.
    /// </summary>
    public void SweepOrphanedMedia()
    {
        try
        {
            if (!Directory.Exists(_mediaFolder)) return;

            HashSet<string> referenced;
            lock (_sync)
            {
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
                    if (!staleTemp && !staleAge) continue;

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

                string newName = $"{item.Id}.r{RotationFileSuffix}.svault";
                string newPath = Path.Combine(_mediaFolder, newName);
                if (File.Exists(newPath)) TryDelete(newPath);

                long itemBytes = Math.Max(0, item.FileSizeBytes);
                long processedSnapshot = processed;
                var chunkProgress = new Progress<double>(v =>
                {
                    double overall = (double)(processedSnapshot + (long)(itemBytes * v)) / totalBytes;
                    progress?.Report(Math.Clamp(overall * 0.95, 0, 0.95));
                });

                await Task.Run(() => CryptoService.ReEncryptFile(oldPath, newPath, oldKey, newKey, chunkProgress, cancellationToken), cancellationToken);
                createdFiles.Add(newPath);
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
            }

            // Config is committed: old files are now unreferenced garbage.
            foreach (var (_, oldName, _) in planned)
            {
                try
                {
                    CryptoService.ShredFile(Path.Combine(_mediaFolder, oldName), 1);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"更换密码后旧文件清理失败: {ex.GetType().Name}");
                }
            }

            DeleteJournal();
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

            RotationJournal? journal;
            try
            {
                byte[] cipher = File.ReadAllBytes(_rotationJournalPath);
                byte[] plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                journal = JsonSerializer.Deserialize<RotationJournal>(Encoding.UTF8.GetString(plain));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"密码更换日志损坏，已丢弃: {ex.GetType().Name}");
                DeleteJournal();
                return;
            }

            if (journal == null || journal.Files.Count == 0)
            {
                DeleteJournal();
                return;
            }

            bool committed;
            lock (_sync)
            {
                committed = _config != null &&
                            (journal.IsDecoy
                                ? string.Equals(_config.DecoyHashHex, journal.NewHashHex, StringComparison.OrdinalIgnoreCase)
                                : string.Equals(_config.RealHashHex, journal.NewHashHex, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var pair in journal.Files)
            {
                if (!IsSafeFileName(pair.OldName) || !IsSafeFileName(pair.NewName)) continue;
                if (committed)
                {
                    TryDelete(Path.Combine(_mediaFolder, pair.OldName));
                }
                else
                {
                    TryDelete(Path.Combine(_mediaFolder, pair.NewName));
                }
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

    private void LoadConfig()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_configPath) && !File.Exists(_configBackupPath))
                {
                    _config = new VaultConfig();
                    _corrupted = false;
                    return;
                }

                VaultConfig? loaded = TryLoadConfigFile(_configPath);
                if (loaded == null)
                {
                    AppLog.Warn("保险箱配置无法读取，尝试备份");
                    loaded = TryLoadConfigFile(_configBackupPath);
                    if (loaded != null)
                    {
                        try { File.Copy(_configBackupPath, _configPath, overwrite: true); } catch { }
                        AppLog.Info("已从保险箱配置备份恢复");
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
            if (!File.Exists(path)) return null;
            byte[] encrypted = File.ReadAllBytes(path);
            if (encrypted.Length == 0) return null;
            byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var config = JsonSerializer.Deserialize<VaultConfig>(Encoding.UTF8.GetString(decrypted));
            return config;
        }
        catch (Exception ex)
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

    private void SaveConfigOrThrow()
    {
        lock (_sync)
        {
            if (_config == null) throw new InvalidOperationException("保险箱配置未加载。");

            string json = JsonSerializer.Serialize(_config);
            byte[] plain = Encoding.UTF8.GetBytes(json);
            byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

            string temp = _configPath + ".tmp";
            File.WriteAllBytes(temp, encrypted);

            if (File.Exists(_configPath))
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
                File.Move(temp, _configPath, overwrite: true);
            }
        }
    }

    private void MigrateOldVaultIfPresent()
    {
        try
        {
            string oldStore = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "StealthVault_Store");
            string oldConfig = Path.Combine(oldStore, "vault_config.dat");
            string oldMedia = Path.Combine(oldStore, "EncryptedMedia");

            if (File.Exists(oldConfig) && !File.Exists(_configPath))
            {
                try
                {
                    string json = File.ReadAllText(oldConfig, Encoding.UTF8);
                    var migrated = JsonSerializer.Deserialize<VaultConfig>(json);
                    if (migrated != null)
                    {
                        SanitizeConfig(migrated);
                        lock (_sync)
                        {
                            _config = migrated;
                            SaveConfigOrThrow();
                        }
                        AppLog.Info("已迁移旧版保险箱配置");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"旧版保险箱配置迁移失败: {ex.GetType().Name}");
                }
            }

            if (Directory.Exists(oldMedia))
            {
                Directory.CreateDirectory(_mediaFolder);
                foreach (string file in Directory.GetFiles(oldMedia))
                {
                    string target = Path.Combine(_mediaFolder, Path.GetFileName(file));
                    if (!File.Exists(target))
                    {
                        File.Copy(file, target, overwrite: false);
                    }
                }
            }

            if (Directory.Exists(oldStore))
            {
                try { Directory.Delete(oldStore, recursive: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("旧版保险箱迁移失败", ex);
        }
    }

    #endregion

    #region Helpers

    private bool HasVaultCore => _config != null && (IsHexHash(_config.RealHashHex) || _corrupted);

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

    private void ValidateItem(VaultItem item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        if (!IsSafeFileName(item.StoredFileName)) throw new InvalidOperationException("媒体记录无效。");

        lock (_sync)
        {
            bool inReal = _config?.Items.Any(i => i.Id == item.Id) == true;
            bool inDecoy = _config?.DecoyItems.Any(i => i.Id == item.Id) == true;
            if (!inReal && !inDecoy) throw new InvalidOperationException("该媒体不属于当前保险箱。");
            if (_isDecoyMode ? !inDecoy : !inReal) throw new InvalidOperationException("该媒体不属于当前保险箱空间。");
        }
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

    /// <summary>Replaces the visible collection. Must be called on the UI thread.</summary>
    public void RefreshVisibleItems()
    {
        lock (_sync)
        {
            RefreshVisibleItemsLocked();
        }
    }

    private void RefreshVisibleItemsLocked()
    {
        VisibleItems.Clear();
        if (!_isUnlocked || _config == null) return;

        var source = _isDecoyMode ? _config.DecoyItems : _config.Items;
        foreach (VaultItem item in source)
        {
            item.IsDecoy = _isDecoyMode;
            VisibleItems.Add(item);
        }
    }

    private bool IsInsideVault(string fullPath)
    {
        try
        {
            string vaultFull = Path.GetFullPath(_vaultRoot) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(fullPath);
            return candidate.StartsWith(vaultFull, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".heic" => "Image",
            ".mp3" or ".flac" or ".wav" or ".m4a" or ".aac" or ".ogg" => "Audio",
            ".pdf" or ".txt" or ".doc" or ".docx" or ".zip" or ".7z" or ".md" => "Document",
            _ => "Other"
        };
    }

    #endregion
}
