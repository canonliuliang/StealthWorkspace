using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private BookmarkLibrary? _barSnapshot;
    private SettingsService.BookmarkSession? _railSession;
    private BookmarkManager? _railManager;
    private CancellationTokenSource? _bookmarkImport;

    internal BookmarkManager? ActiveBarEditor => BookmarkManagerHost.Visibility == Visibility.Visible ? _railManager : null;

    private void BookmarkBar_Loaded(object sender, RoutedEventArgs args) => RefreshBookmarkBar();

    private void RefreshBookmarkBar()
    {
        if (_isDisposed || !IsLoaded || BookmarkManagerHost == null) return;
        var settings = SettingsService.Instance;
        var session = settings.ActiveBookmarkSession;
        if (!ReferenceEquals(_railSession, session))
        {
            ClearBookmarkRail();
            _railSession = session;
        }
        BookmarkBar.Visibility = session == null ? Visibility.Collapsed : Visibility.Visible;
        if (session == null) return;

        var library = settings.GetBookmarkLibrary();
        bool changed = !Equals(_barSnapshot, library);
        bool wide = ActualWidth >= 840;
        bool expanded = session.RailExpanded ?? (wide && (library.Items.Count != 0 || library.Folders.Count != 0));
        BookmarkBar.Width = expanded ? Math.Clamp(ActualWidth * 0.26, 264, 300) : 40;
        // In a small window an explicitly opened rail overlays the edge of the
        // page. Resizing a narrow website to a phone layout loses reading position.
        bool overlay = expanded && ActualWidth < 680;
        Grid.SetColumnSpan(WebContentArea, overlay ? 2 : 1);
        WebContentArea.Margin = new Thickness(0, 0, overlay ? 40 : 0, 0);
        BookmarkManagerHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ManageBarBookmarks.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        if (!expanded) return;
        _barSnapshot = library;
        if (_railManager == null)
        {
            var manager = new BookmarkManager();
            manager.Open(session, session.RailFolderId);
            manager.ClosePane = CloseBookmarkPane;
            manager.Import = ImportBrowserBookmarksAsync;
            manager.Export = ExportBrowserBookmarksAsync;
            manager.SaveCurrentPage = () =>
            {
                if (!CanBookmarkCurrentPage || !ReferenceEquals(session, settings.ActiveBookmarkSession) ||
                    !TryCreateWebUri(GetCurrentDocumentUrl(), out var address) || address == null) return;
                var existing = settings.FirstBookmark(settings.CurrentBookmarks, address, manager.CurrentFolderId);
                manager.EditPage(existing, existing?.Title ?? CurrentTitle, address.AbsoluteUri);
            };
            manager.OpenUrl = url =>
            {
                if (_isDisposed || !ReferenceEquals(session, settings.ActiveBookmarkSession)) return;

                NavigateToText(url);
            };
            manager.OpenNewTab = (url, background) =>
            {
                if (!_isDisposed && ReferenceEquals(session, settings.ActiveBookmarkSession)) NewTabRequested?.Invoke(url, background);
            };
            _railManager = manager;
            BookmarkManagerHost.Children.Add(manager);
            UpdateBookmarkStarState();
        }
        else
        {
            if (_isSelected) _railManager.SyncFolderFromSession();
            if (changed) _railManager.RefreshLibrary();
        }
    }

    private void ClearBookmarkRail()
    {
        BookmarkManagerHost.Children.Clear();
        _railManager = null;
        _railSession = null;
        _barSnapshot = null;
    }

    private void ManageBookmarks_Click(object sender, RoutedEventArgs args)
    {
        CancelInitialAddressFocus();
        if (SettingsService.Instance.ActiveBookmarkSession is not { } session) return;
        session.RailExpanded = true;
        RefreshBookmarkBar();
        _railManager?.FocusSearch();
    }

    private void ShowBookmarkResult(string message, bool saved)
    {
        if (_isDisposed || !IsLoaded || SettingsService.Instance.ActiveBookmarkSession is not { } session) return;
        session.RailExpanded = true;
        RefreshBookmarkBar();
        _railManager?.ShowImportResult(message, saved);
    }

    private void StartPageScroller_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        StartContent.MaxWidth = Math.Clamp(args.NewSize.Width - 48, 1, 740);
    }

    private void CloseBookmarkPane()
    {
        if (SettingsService.Instance.ActiveBookmarkSession is not { } session) return;
        session.RailExpanded = false;
        RefreshBookmarkBar();
        ManageBarBookmarks.Focus(FocusState.Programmatic);
    }

    private async Task ExportBrowserBookmarksAsync()
    {
        var settings = SettingsService.Instance;
        if (_isDisposed || _bookmarkImport != null || settings.PersonalBookmarkSession is not { } session || MainWindow.Current == null) return;
        using var lifetime = new CancellationTokenSource();
        _bookmarkImport = lifetime;
        string? status = null;
        bool saved = false;
        try
        {
            var warning = new ContentDialog { XamlRoot = XamlRoot, Title = "导出明文收藏？", Content = "任何能读取文件的人都能查看网页标题和完整网址。", PrimaryButtonText = "选择保存位置", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            _bookmarkDialog = warning;
            ContentDialogResult confirmation;
            try { confirmation = await WorkspaceDialogs.ShowAsync(warning); }
            finally { _bookmarkDialog = null; }
            if (confirmation != ContentDialogResult.Primary || lifetime.IsCancellationRequested || !ReferenceEquals(session, settings.PersonalBookmarkSession)) return;
            var picker = new FileSavePicker(MainWindow.Current.AppWindow.Id) { SuggestedFileName = "Stealth-bookmarks", DefaultFileExtension = ".html" };
            picker.FileTypeChoices.Add("HTML", new List<string> { ".html", ".htm" });
            var result = await WorkspaceNativeDialogs.PickAsync(picker.PickSaveFileAsync);
            if (result == null || lifetime.IsCancellationRequested || !ReferenceEquals(session, settings.PersonalBookmarkSession)) return;
            var library = settings.GetBookmarkLibrary();
            int written = await VaultService.Instance.ExportBookmarksAsync(result.Path, library.Items, lifetime.Token, library.Folders);
            saved = true;
            status = $"已导出 {written} 条收藏。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { status = "导出失败，请检查保存位置。"; AppLog.Warn("收藏导出失败: " + ex.GetType().Name); }
        finally { _bookmarkImport = null; }
        if (status != null && !lifetime.IsCancellationRequested && ReferenceEquals(session, settings.PersonalBookmarkSession)) ShowBookmarkResult(status, saved);
    }

    private async Task ImportBrowserBookmarksAsync()
    {
        var settings = SettingsService.Instance;
        if (_isDisposed || _bookmarkImport != null || _bookmarkDialog != null || settings.PersonalBookmarkSession is not { } session || MainWindow.Current == null) return;
        using var lifetime = new CancellationTokenSource();
        _bookmarkImport = lifetime;
        string? message = null;
        bool saved = false;
        try
        {
            var picker = new FileOpenPicker(MainWindow.Current.AppWindow.Id) { CommitButtonText = "导入收藏" };
            picker.FileTypeFilter.Add(".html");
            picker.FileTypeFilter.Add(".htm");
            var result = await WorkspaceNativeDialogs.PickAsync(picker.PickSingleFileAsync);
            if (result == null || !ReferenceEquals(session,settings.PersonalBookmarkSession) || lifetime.IsCancellationRequested) return;
            var token = lifetime.Token;
            message = await Task.Run(async () =>
            {
                var parsed = await BookmarkHtml.ReadAsync(result.Path, token);
                if (!settings.ImportBookmarkLibrary(parsed, session, token, out int links, out int folders)) throw new IOException("无法保存收藏。");
                return $"已导入 {links} 条收藏、{folders} 个文件夹。重复 {parsed.Items.Count - links} 条，跳过 {parsed.Skipped} 个不支持的网址。";
            }, token);
            saved = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            message = ex is InvalidDataException ? ex.Message : "导入未完成。请选择 Chrome 书签管理器导出的 HTML 文件，并确认文件可以读取。";
            AppLog.Warn("浏览器收藏导入未完成: " + ex.GetType().Name);
        }
        finally { _bookmarkImport = null; }
        if (message != null && !lifetime.IsCancellationRequested && ReferenceEquals(settings.PersonalBookmarkSession,session))
            ShowBookmarkResult(message, saved);
    }
}
