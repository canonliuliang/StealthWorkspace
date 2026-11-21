namespace StealthWorkspace.Services;

internal sealed class BrowserHistoryUnavailableException(string message, Exception? cause = null) : Exception(message, cause);

// All tabs in one environment share the same native operation. A caller's
// waiting deadline never cancels that operation or permits early navigation.
internal sealed class BrowserEntryHistoryGate
{
    private readonly object _gate = new();
    private uint _process;
    private Task? _operation;

    internal Task? Current
    {
        get { lock (_gate) return _operation; }
    }

    internal async Task WaitForDrainAsync()
    {
        if (Current is not { } operation) return;
        try { await operation.ConfigureAwait(false); }
        catch { /* The shutdown controller will retry once this operation has reached its terminal state. */ }
    }

    internal async Task WaitAsync(uint process, Func<Task> clear, TimeSpan deadline)
    {
        Task operation;
        lock (_gate)
        {
            if (_operation == null || _process != process || _operation.IsCanceled || _operation.IsFaulted)
            {
                _process = process;
                _operation = AfterCompletionAsync(_operation, clear);
            }
            operation = _operation;
        }
        try { await operation.WaitAsync(deadline); }
        catch (TimeoutException ex) when (!operation.IsCompleted)
        {
            throw new BrowserHistoryUnavailableException("上次的浏览记录仍在清理，网页尚未打开。请稍后点击重试。", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BrowserHistoryUnavailableException("浏览记录清理未完成，网页尚未打开。请点击重试。", ex);
        }
    }

    private static async Task AfterCompletionAsync(Task? previous, Func<Task> next)
    {
        if (previous != null)
        {
            try { await previous; }
            catch { /* The previous operation has reached a terminal state; retry is now safe. */ }
        }
        await next();
    }
}
