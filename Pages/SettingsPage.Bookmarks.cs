using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using StealthWorkspace.Services;

namespace StealthWorkspace.Pages;

public sealed partial class SettingsPage
{
    private CancellationTokenSource? _bookmarkTransfer;

    private async void ImportBookmarks_Click(object sender, RoutedEventArgs e) => await TransferBookmarksAsync(false);
    private async void ExportBookmarks_Click(object sender, RoutedEventArgs e) => await TransferBookmarksAsync(true);

    private async Task TransferBookmarksAsync(bool export)
    {
        var settings = SettingsService.Instance;
        var session = settings.PersonalBookmarkSession;
        if (session == null || _bookmarkTransfer != null || _picking || MainWindow.Current == null || VaultService.Instance.IsDecoyMode
            || !BeginWork(export ? "导出收藏" : "导入收藏")) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_work!.Token);
        _bookmarkTransfer = lifetime;
        var token = lifetime.Token;
        try
        {
            var id = MainWindow.Current.AppWindow.Id;
            if (export)
            {
                var confirmation = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "导出明文收藏？", Content = "任何能读取此 HTML 文件的人都可以查看网页标题和完整网址，包括链接中的查询参数。",
                    PrimaryButtonText = "选择保存位置", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
                };
                if (await WorkspaceDialogs.ShowAsync(confirmation) != ContentDialogResult.Primary) return;
                token.ThrowIfCancellationRequested();
                var picker = new FileSavePicker(id) { SuggestedFileName = "Stealth-bookmarks", DefaultFileExtension = ".html" };
                picker.FileTypeChoices.Add("HTML", new List<string> { ".html", ".htm" });
                var result = await WorkspaceNativeDialogs.PickAsync(picker.PickSaveFileAsync);
                if (result == null) return;
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(session, settings.PersonalBookmarkSession)) return;
                var snapshot = settings.GetBookmarkLibrary();
                int written = await VaultService.Instance.ExportBookmarksAsync(result.Path, snapshot.Items, token, snapshot.Folders);
                ReportBookmarkTransfer($"已导出 {written} 条收藏和 {snapshot.Folders.Count} 个文件夹，跳过 {snapshot.Items.Count - written} 条不支持的网址。", token);
            }
            else
            {
                var picker = new FileOpenPicker(id);
                picker.FileTypeFilter.Add(".html");
                picker.FileTypeFilter.Add(".htm");
                var result = await WorkspaceNativeDialogs.PickAsync(picker.PickSingleFileAsync);
                if (result == null) return;
                token.ThrowIfCancellationRequested();
                var parsed = await Task.Run(() => BookmarkHtml.ReadAsync(result.Path, token), token);
                token.ThrowIfCancellationRequested();
                if (parsed.Items.Count == 0 && parsed.Folders.Count == 0)
                {
                    ReportBookmarkTransfer("文件中没有可导入的 HTTP 或 HTTPS 网页收藏。", token);
                    return;
                }
                var (added, folders) = await Task.Run(() => settings.ImportBookmarkLibrary(parsed, session, token, out int count, out int groups)
                    ? (count, groups) : throw new IOException("无法保存收藏。请检查设置文件能否写入后重试。"), token);
                ReportBookmarkTransfer($"已导入 {added} 条收藏和 {folders} 个文件夹，已有或重复 {parsed.Items.Count - added} 条，跳过 {parsed.Skipped} 条不支持的网址。", token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ReportBookmarkTransfer(ex is InvalidDataException ? ex.Message : "操作未完成。请检查文件大小、格式、访问权限和剩余空间后重试。", token);
            AppLog.Warn("收藏迁移未完成", ex);
        }
        finally
        {
            _bookmarkTransfer = null;
            EndWork();
            if (IsLoaded) UpdateActions();
        }
    }

    private void ReportBookmarkTransfer(string message, CancellationToken token)
    {
        if (!_isLoaded || token.IsCancellationRequested) return;
        BookmarkTransferStatus.Text = message;
        BookmarkTransferStatus.Visibility = Visibility.Visible;
    }
}
