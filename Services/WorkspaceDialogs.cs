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
        if (Active.Any(p => !p.Dismissed)) return ContentDialogResult.None;

        if (dialog.RequestedTheme == Microsoft.UI.Xaml.ElementTheme.Default &&
            dialog.XamlRoot?.Content is Microsoft.UI.Xaml.FrameworkElement rootElement &&
            rootElement.RequestedTheme != Microsoft.UI.Xaml.ElementTheme.Default)
        {
            dialog.RequestedTheme = rootElement.RequestedTheme;
        }

        var presentation = new Presentation(dialog);
        Active.Add(presentation);
        try
        {
            var result = await dialog.ShowAsync();
            return presentation.Dismissed ? ContentDialogResult.None : result;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            AppLog.Warn("显示工作区对话框冲突，已忽略重复弹窗", ex);
            return ContentDialogResult.None;
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
        WorkspaceNativeDialogs.DismissForPrivacy(MainWindow.CurrentHandle);
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
