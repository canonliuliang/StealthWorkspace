using System.Security.Cryptography;
using StealthWorkspace.Models;

namespace StealthWorkspace.Services;

public partial class VaultService
{
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private SynchronizationContext? _uiContext;
    private int _uiThreadId;
    private int _publishingItems;
    private int _itemsRefreshPending;

    private sealed class MutationLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    private async Task<IDisposable> EnterMutationAsync(CancellationToken token = default, bool allowPendingRecovery = false, bool allowPendingRemoval = false)
    {
        await _mutationGate.WaitAsync(token);
        try
        {
            if (!allowPendingRecovery && File.Exists(_rotationJournalPath))
                throw new InvalidOperationException("存在尚未完成的密码更换恢复记录，原文件已保留。请先重新打开保险箱并检查恢复状态。");
            if (!allowPendingRemoval && !IsDefinitelyMissing(RemovalJournalPath))
                throw new InvalidOperationException("上次删除或重置尚未完成。请重试该操作或重新打开保险箱完成恢复。");
        }
        catch
        {
            _mutationGate.Release();
            throw;
        }
        return new MutationLease(_mutationGate);
    }

    private static Task RejectOtherSpacePasswordAsync(string password, string saltHex, string hashHex,
        int iterations, CancellationToken token) => Task.Run(() =>
    {
        if (!IsHexHash(hashHex)) return;
        if (!IsHexSalt(saltHex)) throw new InvalidDataException("另一个保险箱空间的密码校验信息不可用。");
        byte[] candidate = CryptoService.DeriveKey(password, Convert.FromHexString(saltHex),
            iterations > 0 ? iterations : CryptoService.DefaultIterations);
        try
        {
            if (CryptoService.VerifyKeyHash(candidate, hashHex))
                throw new ArgumentException("主密码与诱饵密码不能相同。");
        }
        finally { CryptographicOperations.ZeroMemory(candidate); }
    }, token);

    // Bind explicitly from the window, not from a background singleton initializer.
    internal void BindToUiContext(SynchronizationContext context)
    {
        _uiThreadId = Environment.CurrentManagedThreadId;
        _uiContext = context ?? throw new ArgumentNullException(nameof(context));
        RefreshVisibleItems();
    }

    private void OnUiContext(Action action)
    {
        var context = _uiContext;
        if (context == null || Environment.CurrentManagedThreadId == _uiThreadId) action();
        else context.Post(_ => action(), null);
    }

    private void PublishVaultState(bool unlocked, bool notify = true) => OnUiContext(() =>
    {
        RefreshVisibleItems();
        if (!notify || IsUnlocked != unlocked) return;
        if (unlocked) Unlocked?.Invoke();
        else Locked?.Invoke();
    });

    /// <summary>Publishes a current snapshot on the bound UI context, outside the state lock.</summary>
    public void RefreshVisibleItems() => OnUiContext(PublishVisibleItems);

    private void PublishVisibleItems()
    {
        Interlocked.Exchange(ref _itemsRefreshPending, 1);
        if (Interlocked.CompareExchange(ref _publishingItems, 1, 0) != 0) return;
        try
        {
            while (Interlocked.Exchange(ref _itemsRefreshPending, 0) != 0)
            {
                VaultItem[] snapshot;
                lock (_sync)
                    snapshot = !_isUnlocked || _config == null ? Array.Empty<VaultItem>()
                        : (_isDecoyMode ? _config.DecoyItems : _config.Items).ToArray();

                // ObservableCollection raises synchronous UI callbacks here. Never hold _sync.
                // Retain the unchanged prefix so importing one item does not rebuild every card.
                if (snapshot.Length == 0) { if (VisibleItems.Count != 0) VisibleItems.Clear(); continue; }
                for (int i = 0; i < snapshot.Length; i++)
                {
                    if (i == VisibleItems.Count) VisibleItems.Add(snapshot[i]);
                    else if (!ReferenceEquals(VisibleItems[i], snapshot[i])) VisibleItems[i] = snapshot[i];
                    if (Volatile.Read(ref _itemsRefreshPending) != 0) break;
                }
                if (Volatile.Read(ref _itemsRefreshPending) != 0) continue;
                while (VisibleItems.Count > snapshot.Length) VisibleItems.RemoveAt(VisibleItems.Count - 1);
            }
        }
        finally
        {
            Volatile.Write(ref _publishingItems, 0);
            if (Volatile.Read(ref _itemsRefreshPending) != 0) RefreshVisibleItems();
        }
    }
}
