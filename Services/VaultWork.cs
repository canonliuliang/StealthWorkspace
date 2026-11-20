namespace StealthWorkspace.Services;

/// <summary>One user-initiated vault operation at a time across all pages of the window.</summary>
internal sealed class VaultWork : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static VaultWork? Current { get; private set; }
    internal static bool IsBusy => Current != null;
    internal static event Action? Changed;

    internal string Label { get; private set; }
    internal int? Percent { get; private set; }
    internal CancellationToken Token { get; }
    internal Task Completion => _finished.Task;

    private VaultWork(string label)
    {
        Label = label;
        Token = _lifetime.Token;
    }

    // Starts, progress callbacks and disposal run on the window's UI thread.
    internal static VaultWork? TryBegin(string label)
    {
        if (IsBusy || !VaultService.Instance.IsUnlocked) return null;
        var work = new VaultWork(label);
        Current = work;
        VaultService.Instance.Locked += work.Cancel;
        Changed?.Invoke();
        return work;
    }

    internal void SetPhase(string label)
    {
        if (Current != this || Token.IsCancellationRequested) return;
        Label = label;
        Percent = null;
        Changed?.Invoke();
    }

    internal void Report(double fraction)
    {
        if (Current != this || Token.IsCancellationRequested || !double.IsFinite(fraction)) return;
        int percent = (int)Math.Floor(Math.Clamp(fraction, 0, 1) * 100);
        if (Percent >= percent) return;
        Percent = percent;
        Changed?.Invoke();
    }

    private void Cancel()
    {
        _lifetime.Cancel();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (Current != this) return;
        VaultService.Instance.Locked -= Cancel;
        Current = null;
        _lifetime.Dispose();
        _finished.TrySetResult();
        Changed?.Invoke();
    }
}
