using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private bool CanBookmarkCurrentPage { get; set; }
    private ContentDialog? _bookmarkDialog;

    private bool _savingBookmark;

    private void UpdateBookmarkStarState()
    {
        if (_isDisposed) return;
        string url = GetCurrentDocumentUrl();
        bool valid = TryCreateWebUri(url, out var address) && SettingsService.Instance.ActiveBookmarkSession != null;
        bool bookmarked = valid && SettingsService.Instance.IsBookmarked(url);
        bool privateAddress = valid && address!.UserInfo.Length != 0 && !bookmarked;
        CanBookmarkCurrentPage = valid && !privateAddress && !_savingBookmark;
        string action = _savingBookmark ? "正在保存收藏" : bookmarked ? (SettingsService.Instance.HasBookmarkFolders ? "编辑收藏" : "取消收藏") : privateAddress ? "无法收藏含登录信息的网址" : "收藏本页";
        _railManager?.SetCurrentPageStatus(CanBookmarkCurrentPage, bookmarked ? "编辑本页收藏" : action);
    }

    internal async void ToggleBookmarkFromShortcut()
    {
        if (_isDisposed || _savingBookmark) return;
        string url = GetCurrentDocumentUrl();
        if (!TryCreateWebUri(url, out var address)) return;
        var settings = SettingsService.Instance;
        bool bookmarked = settings.IsBookmarked(url);
        if (!bookmarked && address!.UserInfo.Length != 0) return;
        string title = string.IsNullOrWhiteSpace(CurrentTitle) ? url : CurrentTitle;
        var library = settings.GetBookmarkLibrary();
        if (library.Folders.Count != 0 && settings.ActiveBookmarkSession is { } current)
        {
            CancelInitialAddressFocus();
            current.RailExpanded = true;
            RefreshBookmarkBar();
            var existing = settings.FirstBookmark(library.Items, address!, _railManager?.CurrentFolderId);
            _railManager?.EditPage(existing, existing?.Title ?? title, url);
            return;
        }
        await SaveBookmarkEditAsync(settings.ActiveBookmarkSession, session => bookmarked ?
            settings.RemoveBookmark(url, session) : settings.AddBookmark(title, url, session));
    }

    private async void BookmarkDelete_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: BookmarkItem item }) return;
        var settings = SettingsService.Instance;
        await SaveBookmarkEditAsync(settings.ActiveBookmarkSession, session => settings.RemoveBookmarkEntry(session, item));
    }

    private async Task<bool> SaveBookmarkEditAsync(SettingsService.BookmarkSession? session, Func<SettingsService.BookmarkSession, bool> change)
    {
        if (session == null || _isDisposed || _savingBookmark) return false;
        _savingBookmark = true;
        UpdateBookmarkStarState();
        try
        {
            bool saved = await Task.Run(() => change(session));
            if (_isDisposed || !ReferenceEquals(session, SettingsService.Instance.ActiveBookmarkSession)) return false;
            if (!saved) ShowBookmarkSaveError();
            return saved;
        }
        catch (Exception ex)
        {
            AppLog.Warn("编辑收藏失败: " + ex.GetType().Name);
            if (!_isDisposed && ReferenceEquals(session, SettingsService.Instance.ActiveBookmarkSession)) ShowBookmarkSaveError();
            return false;
        }
        finally
        {
            _savingBookmark = false;
            UpdateBookmarkStarState();
        }
    }

    private void ShowBookmarkSaveError() => ShowBrowserError("收藏未保存", "无法写入收藏数据，原来的收藏已保留。请检查磁盘空间或文件占用后重试。");

    internal async Task CheckBookmarkEditWorkflowAsync()
    {
        if (!AppPaths.HasDataRootOverride) throw new InvalidOperationException("Bookmark audit requires an isolated workspace");
        await using var site = new BrowserHttpFixture();
        string address = site.Address + "/page?bookmark-audit";
        await NavigateAuditAsync(address);
        string url = GetCurrentDocumentUrl();
        if (url != address || SettingsService.Instance.IsBookmarked(url)) throw new InvalidOperationException("Bookmark fixture URL mismatch");
        ToggleBookmarkFromShortcut();
        bool disabledWhileSaving = _savingBookmark && !CanBookmarkCurrentPage;
        ToggleBookmarkFromShortcut();
        await WaitForSaveAsync();
        var saved = SettingsService.Instance.CurrentBookmarks.Single(b => b.Url == url);
        if (!disabledWhileSaving || !CanBookmarkCurrentPage || !await Task.Run(() => new SettingsService().IsBookmarked(url)))
            throw new InvalidOperationException("Bookmark command failed to commit exactly once");

        BookmarkDelete_Click(new MenuFlyoutItem { Tag = saved }, new RoutedEventArgs());
        await WaitForSaveAsync();
        if (SettingsService.Instance.IsBookmarked(url) || await Task.Run(() => new SettingsService().IsBookmarked(url)))
            throw new InvalidOperationException("Bookmark delete failed to reach disk");
        SmokeTest.Report("audit: native bookmark command persisted once, duplicate shortcut ignored and menu deletion persisted");

        async Task WaitForSaveAsync()
        {
            for (int i = 0; i < 200 && _savingBookmark; i++) await Task.Delay(25);
            if (_savingBookmark) throw new TimeoutException("Bookmark command remained busy");
        }
    }

    private void DetachBookmarkUi()
    {
        _bookmarkImport?.Cancel();
        ClearBookmarkRail();
        _bookmarkDialog?.Hide();
    }
}
