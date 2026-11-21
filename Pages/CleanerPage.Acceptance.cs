using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using StealthWorkspace.Services;

namespace StealthWorkspace.Pages;

public sealed partial class CleanerPage
{
    // Exercise both native buttons. The diagnostic data root isolates this flow from Windows history and real downloads.
    internal async Task VerifySyntheticCleanAsync()
    {
        if (!SmokeTest.UiSmoke || !AppPaths.HasDataRootOverride) throw new InvalidOperationException("A diagnostic vault is required.");
        const string message = "Cleaner fixture did not match the displayed outcome.";
        async Task WaitUntil(Func<bool> ready)
        {
            for (int i = 0; i < 200; i++)
            {
                if (ready()) return;
                await Task.Delay(40);
            }
            throw new InvalidOperationException(message);
        }
        async Task ConfirmClean()
        {
            MediaAcceptance.Invoke(CleanBtn);
            Button? confirm = null;
            await WaitUntil(() => (confirm = VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot)
                .Select(p => ImageExperienceAcceptance.FindConfirm(p.Child, "确认清理")).FirstOrDefault(b => b != null)) != null);
            MediaAcceptance.Invoke(confirm!);
            await WaitUntil(() => !_isBusy && StatusInfoBar.IsOpen);
        }

        await WaitUntil(() => IsLoaded && Items.Count > 0 && !_isBusy);
        if (Items.Single(i => i.IsSelected).Category != CleanerCategory.TempFiles) throw new InvalidOperationException(message);
        string directory = Directory.CreateDirectory(Path.Combine(AppPaths.DataRoot, "CleanerTemp")).FullName;
        string old = Path.Combine(directory, "old.tmp"), held = Path.Combine(directory, "in-use.tmp"), fresh = Path.Combine(directory, "recent.tmp");
        foreach (string file in new[] { old, held, fresh }) File.WriteAllText(file, "synthetic maintenance fixture");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(held, DateTime.UtcNow.AddDays(-2));
        var row = Items.Single(i => i.Category == CleanerCategory.TempFiles);
        using (var reader = File.Open(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await ScanAllAsync();
            if (row.DetectedCount != 1 || !row.Status.Contains("占用中 1 个")) throw new InvalidOperationException(message);
            await ConfirmClean();
            if (File.Exists(old) || !File.Exists(held) || !File.Exists(fresh) || row.Outcome != CleanOutcome.Skipped ||
                StatusInfoBar.Severity != InfoBarSeverity.Informational || !row.Status.Contains("已删除 1 个")) throw new InvalidOperationException(message);
        }
        await ConfirmClean();
        if (File.Exists(held) || !File.Exists(fresh) || row.Outcome != CleanOutcome.Complete || StatusInfoBar.Severity != InfoBarSeverity.Success)
            throw new InvalidOperationException(message);
        await ConfirmClean();
        if (row.Outcome != CleanOutcome.NothingToClean || StatusInfoBar.Severity != InfoBarSeverity.Informational ||
            AccessPanel.Visibility == Visibility.Visible != CleanerAccess.IsRestricted) throw new InvalidOperationException(message);
        SmokeTest.Report("cleaner: actual confirmation deleted the old file, retained recent and locked files, retried after release and reported the empty result; low integrity=" + CleanerAccess.IsRestricted);
    }
}
