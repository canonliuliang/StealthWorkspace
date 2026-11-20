using System.Security.Cryptography;

namespace StealthWorkspace.Services;

public enum VaultUnlockStatus
{
    Success, WrongPassword, LockedOut, NoVault, Corrupted, Error
}

/// <summary>A single-use verification result. Dispose results that are not applied.</summary>
public sealed class VaultUnlockResult : IDisposable
{
    public VaultUnlockStatus Status { get; init; }
    public bool IsDecoy { get; init; }
    public int RetryAfterSeconds { get; init; }
    public string Message { get; init; } = string.Empty;
    internal byte[]? Key;
    internal object? Owner { get; init; }
    internal long Epoch { get; init; }

    internal byte[]? TakeKey()
    {
        byte[]? key = Interlocked.Exchange(ref Key, null);
        GC.SuppressFinalize(this);
        return key;
    }

    internal void ForgetKey()
    {
        byte[]? key = TakeKey();
        if (key != null) CryptographicOperations.ZeroMemory(key);
    }

    public void Dispose() => ForgetKey();
    ~VaultUnlockResult() => ForgetKey();
}

public partial class VaultService
{
    private const int MaxFailedAttemptsBeforeDelay = 5;
    private const int LockoutSecondsShort = 30;
    private const int LockoutSecondsLong = 300;
    // Serialize password attempts without holding the state lock needed by the UI/boss key.
    private readonly object _verificationGate = new();
    private int _failedAttempts;
    private DateTime _lockoutUntilUtc = DateTime.MinValue;
    private long _unlockEpoch;

    private sealed record UnlockSnapshot(long Epoch, string RealSalt, string RealHash, int Iterations,
        string DecoySalt, string DecoyHash, int DecoyIterations);

    private long CaptureUnlockEpoch()
    {
        lock (_sync) return _unlockEpoch;
    }

    /// <summary>Captures this request before scheduling, so a later lock also revokes queued work.</summary>
    public Task<VaultUnlockResult> VerifyPasswordAsync(string password)
    {
        long requestedEpoch = CaptureUnlockEpoch();
        return Task.Run(() => VerifyPasswordCore(password, requestedEpoch));
    }

    /// <summary>Run off the UI thread. Capture before waiting for another password attempt.</summary>
    public VaultUnlockResult VerifyPassword(string password) => VerifyPasswordCore(password, CaptureUnlockEpoch());

    private VaultUnlockResult VerifyPasswordCore(string password, long requestedEpoch)
    {
        lock (_verificationGate)
        {
            byte[]? key = null;
            try
            {
                // Recovery can change the catalog after a file handle is released.
                // Take the password snapshot after this attempt, off the UI thread.
                if (!IsUnlocked && _mutationGate.Wait(0))
                {
                    try { RecoverPendingOperations(); }
                    finally { _mutationGate.Release(); }
                }
                UnlockSnapshot snapshot;
                lock (_sync)
                {
                    if (_unlockEpoch != requestedEpoch)
                        return new() { Status = VaultUnlockStatus.Error, Message = "保险箱状态已改变，请重新解锁。" };
                    if (_corrupted)
                        return new() { Status = VaultUnlockStatus.Corrupted, Message = "保险箱配置损坏，无法验证密码。" };
                    if (_config == null || !IsHexHash(_config.RealHashHex))
                        return new() { Status = VaultUnlockStatus.NoVault, Message = "尚未创建保险箱。" };
                    DateTime now = DateTime.UtcNow;
                    if (now < _lockoutUntilUtc)
                    {
                        int remaining = Math.Max(1, (int)Math.Ceiling((_lockoutUntilUtc - now).TotalSeconds));
                        return new() { Status = VaultUnlockStatus.LockedOut, RetryAfterSeconds = remaining,
                            Message = $"尝试次数过多，请在 {remaining} 秒后重试。" };
                    }
                    if (string.IsNullOrEmpty(password))
                        return new() { Status = VaultUnlockStatus.WrongPassword, Message = "请输入密码。" };
                    if (password.Length > 1024)
                        return RegisterFailure(now);
                    snapshot = new(requestedEpoch, _config.RealSaltHex, _config.RealHashHex, _config.Iterations,
                        _config.DecoySaltHex, _config.DecoyHashHex, _config.DecoyIterations);
                }

                key = CryptoService.DeriveKey(password, Convert.FromHexString(snapshot.RealSalt), snapshot.Iterations);
                bool matched = CryptoService.VerifyKeyHash(key, snapshot.RealHash);
                bool decoy = false;
                if (!matched && IsHexHash(snapshot.DecoyHash) && IsHexSalt(snapshot.DecoySalt))
                {
                    CryptographicOperations.ZeroMemory(key);
                    key = null;
                    key = CryptoService.DeriveKey(password, Convert.FromHexString(snapshot.DecoySalt),
                        snapshot.DecoyIterations > 0 ? snapshot.DecoyIterations : CryptoService.DefaultIterations);
                    matched = CryptoService.VerifyKeyHash(key, snapshot.DecoyHash);
                    decoy = matched;
                }

                lock (_sync)
                {
                    if (!MatchesUnlockSnapshot(snapshot))
                        return new() { Status = VaultUnlockStatus.Error, Message = "保险箱状态已改变，请重新解锁。" };
                    if (!matched) return RegisterFailure(DateTime.UtcNow);
                    ResetThrottle();
                    var result = new VaultUnlockResult { Status = VaultUnlockStatus.Success, IsDecoy = decoy,
                        Key = key, Owner = this, Epoch = snapshot.Epoch };
                    key = null; // Ownership transfers to the single-use result.
                    return result;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("保险箱密码验证失败", ex);
                return new() { Status = VaultUnlockStatus.Error, Message = "保险箱数据不可用。" };
            }
            finally
            {
                if (key != null) CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    private bool MatchesUnlockSnapshot(UnlockSnapshot snapshot) => !_corrupted && _config != null &&
        _unlockEpoch == snapshot.Epoch && _config.RealSaltHex == snapshot.RealSalt &&
        _config.RealHashHex == snapshot.RealHash && _config.Iterations == snapshot.Iterations &&
        _config.DecoySaltHex == snapshot.DecoySalt && _config.DecoyHashHex == snapshot.DecoyHash &&
        _config.DecoyIterations == snapshot.DecoyIterations;

    /// <summary>Applies a result once; a later lock or a changed key rejects the older result.</summary>
    public bool ApplyUnlock(VaultUnlockResult result)
    {
        byte[]? key = result?.TakeKey();
        bool applied = false;
        try
        {
            if (result == null || result.Status != VaultUnlockStatus.Success || key == null) return false;
            lock (_sync)
            {
                if (!ReferenceEquals(result.Owner, this) || result.Epoch != _unlockEpoch || _corrupted || _config == null)
                    return false;
                string expected = result.IsDecoy ? _config.DecoyHashHex : _config.RealHashHex;
                if (!CryptoService.VerifyKeyHash(key, expected)) return false;
                ReplaceCurrentKey(key);
                key = null;
                _unlockEpoch++;
                _isUnlocked = true;
                _isDecoyMode = result.IsDecoy;
                applied = true;
            }
            PublishVaultState(unlocked: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("应用解锁状态失败", ex);
            return applied;
        }
        finally
        {
            if (key != null) CryptographicOperations.ZeroMemory(key);
        }
    }

    public void Lock()
    {
        bool wasUnlocked;
        lock (_sync)
        {
            _unlockEpoch++; // Also revoke verification currently running while already locked.
            wasUnlocked = _isUnlocked;
            _isUnlocked = false;
            _isDecoyMode = false;
            if (_currentKey != null)
            {
                CryptographicOperations.ZeroMemory(_currentKey);
                _currentKey = null;
            }
        }
        PublishVaultState(unlocked: false, notify: wasUnlocked);
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
        return new() { Status = retryAfter > 0 ? VaultUnlockStatus.LockedOut : VaultUnlockStatus.WrongPassword,
            RetryAfterSeconds = retryAfter, Message = retryAfter > 0
                ? $"尝试次数过多，请在 {retryAfter} 秒后重试。" : "密码错误，请重试。" };
    }

    private void ResetThrottle()
    {
        _failedAttempts = 0;
        _lockoutUntilUtc = DateTime.MinValue;
    }
}
