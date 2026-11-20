namespace StealthWorkspace.Services;

internal static class WorkspaceNativeDialogsAudit
{
    internal static async Task CheckRevocationAsync()
    {
        if (!SmokeTest.UiSmoke) throw new InvalidOperationException("Picker audit requires isolated UI diagnostics");
        var broker = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = WorkspaceNativeDialogs.PickAsync(() => broker.Task.AsAsyncOperation());
        WorkspaceNativeDialogs.DismissForPrivacy(0);
        if (await pending.WaitAsync(TimeSpan.FromSeconds(2)) != null || broker.Task.IsCompleted)
            throw new InvalidOperationException("Revocation must release the caller before the native operation finishes");
        broker.SetResult("late-test-path");
        string? selected = await WorkspaceNativeDialogs.PickAsync(() => Task.FromResult("current-test-path").AsAsyncOperation());
        if (await pending != null || selected != "current-test-path")
            throw new InvalidOperationException("Revoked picker affected a later selection");
        SmokeTest.Report("privacy: canceled file picker releases work before broker completion, discards late paths and permits next selection=True");
    }
}
