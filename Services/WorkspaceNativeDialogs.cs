using System.Runtime.InteropServices;
using System.Text;
using Windows.Foundation;

namespace StealthWorkspace.Services;

internal static class WorkspaceNativeDialogs
{
    // Window commands, registration and picker continuations run on the workspace UI thread.
    private sealed class Picker
    {
        internal IAsyncInfo? Operation;
        internal readonly TaskCompletionSource Revoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly List<Picker> Active = new();
    private static long _generation;

    internal static async Task<T?> PickAsync<T>(Func<IAsyncOperation<T>> start) where T : class
    {
        if (MainWindow.Current == null || MainWindow.Current.IsPrivacyCoverVisible) return null;
        var picker = new Picker();
        ++_generation;
        Active.Add(picker);
        async Task<T?> WaitForNativePickerAsync()
        {
            try
            {
                var operation = start();
                picker.Operation = operation;
                if (picker.Revoked.Task.IsCompleted) CancelOperation(picker);
                var result = await operation;
                return picker.Revoked.Task.IsCompleted ? null : result;
            }
            catch (Exception) when (picker.Revoked.Task.IsCompleted) { return null; }
        }

        try
        {
            Task<T?> completion = WaitForNativePickerAsync();
            // A broker can finish canceling after the window is locked or closed. Discard
            // its eventual result and let the caller release its vault work immediately.
            await Task.WhenAny(completion, picker.Revoked.Task);
            return picker.Revoked.Task.IsCompleted ? null : await completion;
        }
        finally { Active.Remove(picker); }
    }

    internal static T? PickWin32<T>(Func<T> show) where T : class
    {
        if (MainWindow.Current == null || MainWindow.Current.IsPrivacyCoverVisible) return null;
        var picker = new Picker();
        ++_generation;
        Active.Add(picker);
        try
        {
            var result = show();
            return picker.Revoked.Task.IsCompleted ? null : result;
        }
        finally { Active.Remove(picker); }
    }

    internal static void DismissForPrivacy(nint owner)
    {
        if (Active.Count == 0) return;
        long generation = ++_generation;
        var pickers = Active.ToArray();
        Active.Clear();
        foreach (var picker in pickers) picker.Revoked.TrySetResult();
        CloseOwnedNativeDialogs(owner);
        foreach (var picker in pickers) CancelOperation(picker);
        _ = FinishDismissalAsync(owner, generation);
    }

    private static void CancelOperation(Picker picker)
    {
        try { picker.Operation?.Cancel(); }
        catch (Exception ex) { AppLog.Warn("Cancel native picker: " + ex.GetType().Name); }
    }

    private static async Task FinishDismissalAsync(nint owner, long generation)
    {
        // A validation/overwrite dialog can keep the file dialog disabled until its
        // own message loop unwinds. A new picker invalidates all previous retries.
        foreach (int delay in new[] { 40, 100, 250, 500, 1000 })
        {
            await Task.Delay(delay);
            if (generation != _generation || owner == 0 || MainWindow.CurrentHandle != owner) return;
            CloseOwnedNativeDialogs(owner);
        }
    }

    private static bool IsOurs(nint window)
    {
        _ = GetWindowThreadProcessId(window, out uint process);
        return process == Environment.ProcessId;
    }

    private static int OwnedDialogDepth(nint dialog, nint owner)
    {
        if (dialog == owner || !IsOurs(dialog)) return 0;
        var name = new StringBuilder(32);
        if (GetClassNameW(dialog, name, name.Capacity) == 0 || name.ToString() != "#32770") return 0;
        nint current = dialog;
        bool pickerInChain = false;
        for (int depth = 1; depth <= 32; depth++)
        {
            if (!pickerInChain) pickerInChain = ContainsPickerShellView(current);
            current = GetWindow(current, 4); // GW_OWNER
            if (current == owner) return pickerInChain ? depth : 0;
            if (current == 0 || !IsOurs(current)) return 0;
        }
        return 0;
    }

    private static bool ContainsPickerShellView(nint window)
    {
        bool found = false;
        var name = new StringBuilder(64);
        EnumChildWindows(window, (child, _) =>
        {
            if (IsOurs(child) && GetClassNameW(child, name, name.Capacity) != 0)
                found = name.ToString() is "SHELLDLL_DefView" or "ExplorerBrowserControl";
            return !found;
        }, 0);
        return found;
    }

    private static void CloseOwnedNativeDialogs(nint owner)
    {
        if (owner == 0 || !IsOurs(owner)) return;
        var dialogs = new List<(nint Handle, int Depth)>();
        EnumWindows((window, _) =>
        {
            int depth = OwnedDialogDepth(window, owner);
            if (depth != 0) dialogs.Add((window, depth));
            return true;
        }, 0);
        foreach (var (window, _) in dialogs.OrderByDescending(d => d.Depth))
        {
            if (OwnedDialogDepth(window, owner) == 0) continue;
            ShowWindowAsync(window, 0); // SW_HIDE, including nested warnings
            nint cancel = GetDlgItem(window, 2); // IDCANCEL
            nint decline = GetDlgItem(window, 7); // IDNO for overwrite prompts
            if (cancel != 0) PostMessageW(window, 0x0111, 2, cancel);
            else if (decline != 0) PostMessageW(window, 0x0111, 7, decline);
            else PostMessageW(window, 0x0010, 0, 0); // WM_CLOSE; never send IDOK
        }
    }

    private delegate bool EnumWindow(nint window, nint state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindow callback, nint state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetClassNameW(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint dialog, int id);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}
