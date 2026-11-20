using Microsoft.UI.Xaml.Controls;

namespace StealthWorkspace.Services;

internal static class WorkspaceDialogs
{
    // Dialogs in this workspace are created in code and shown as native popups.
    // Track the ShowAsync lifetime: a newly requested popup may not be in the
    // visual tree yet, and a button's result may still be waiting for its exit.
    private sealed class Presentation(ContentDialog dialog)
    {
        internal ContentDialog Dialog { get; } = dialog;
        internal double Opacity { get; } = dialog.Opacity;
        internal bool WasHitTestVisible { get; } = dialog.IsHitTestVisible;
        internal bool Dismissed { get; set; }
    }

    // Both presentation and lock handling execute on the window's UI thread.
    private static readonly List<Presentation> Active = new();

    internal static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (MainWindow.Current?.IsPrivacyCoverVisible == true) return ContentDialogResult.None;
        var presentation = new Presentation(dialog);
        var operation = dialog.ShowAsync();
        Active.Add(presentation);
        try
        {
            var result = await operation;
            return presentation.Dismissed ? ContentDialogResult.None : result;
        }
        finally
        {
            Active.Remove(presentation);
            if (presentation.Dismissed)
            {
                dialog.Opacity = presentation.Opacity;
                dialog.IsHitTestVisible = presentation.WasHitTestVisible;
            }
        }
    }

    internal static void DismissForPrivacy()
    {
        foreach (var presentation in Active.ToArray())
        {
            presentation.Dismissed = true;
            var dialog = presentation.Dialog;
            try
            {
                dialog.Opacity = 0;
                dialog.IsHitTestVisible = false;
                dialog.Hide();
            }
            catch (Exception ex)
            {
                // The native popup can already be tearing down with the window.
                AppLog.Warn("关闭工作区对话框失败", ex);
            }
        }
    }
}
