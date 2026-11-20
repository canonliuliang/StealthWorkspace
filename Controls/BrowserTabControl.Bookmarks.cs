using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using StealthWorkspace.Services;
using Windows.System;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private BookmarkItem? _bookmarkToRename;
    private ContentDialog? _bookmarkDialog;
    private BookmarkFilter? _bookmarkFilter;
    private bool _savingBookmark;

    private void UpdateBookmarkStarState()
    {
        if (_isDisposed) return;
        string url = GetCurrentDocumentUrl();
        bool valid = TryCreateWebUri(url, out _) && SettingsService.Instance.ActiveBookmarkSession != null;
        bool bookmarked = valid && SettingsService.Instance.IsBookmarked(url);
        BookmarkStarBtn.IsEnabled = valid && !_savingBookmark;
        string action = _savingBookmark ? "正在保存收藏" : bookmarked ? "取消收藏" : "收藏本页";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BookmarkStarBtn, action);
        ToolTipService.SetToolTip(BookmarkStarBtn, action + " (Ctrl+D)");
        VisualStateManager.GoToState(this, bookmarked ? "Bookmarked" : "NotBookmarked", false);
    }

    private async void BookmarkStarBtn_Click(object sender, RoutedEventArgs args)
    {
        if (_isDisposed || _savingBookmark) return;
        string url = GetCurrentDocumentUrl();
        if (!TryCreateWebUri(url, out _)) return;
        var settings = SettingsService.Instance;
        bool bookmarked = settings.IsBookmarked(url);
        string title = string.IsNullOrWhiteSpace(CurrentTitle) ? url : CurrentTitle;
        bool saved = await SaveBookmarkEditAsync(settings.ActiveBookmarkSession, session => bookmarked ?
            settings.RemoveBookmark(url, session) : settings.AddBookmark(title, url, session));
        if (saved && !_isDisposed && GetCurrentDocumentUrl() == url) UiMotion.Reveal(BookmarkStarIcon, MotionCue.Fade);
    }

    private async void BookmarkDelete_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: BookmarkItem item }) return;
        var settings = SettingsService.Instance;
        await SaveBookmarkEditAsync(settings.ActiveBookmarkSession, session => settings.RemoveBookmark(item.Url, session));
    }

    private async Task<bool> SaveBookmarkEditAsync(SettingsService.BookmarkSession? session, Func<SettingsService.BookmarkSession, bool> change)
    {
        if (session == null || _isDisposed || _savingBookmark) return false;
        _savingBookmark = true;
        if (BookmarksListView != null) BookmarksListView.IsEnabled = false;
        UpdateBookmarkStarState();
        try
        {
            bool saved = await Task.Run(() => change(session));
            if (_isDisposed || !ReferenceEquals(session, SettingsService.Instance.ActiveBookmarkSession)) return false;
            if (!saved) { BookmarksFlyout.Hide(); ShowBookmarkSaveError(); }
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
            if (!_isDisposed && BookmarksListView != null) BookmarksListView.IsEnabled = true;
            UpdateBookmarkStarState();
        }
    }

    private void BookmarkSearch_Changed(object sender, TextChangedEventArgs args) => RefreshBookmarkList();

    private void RefreshBookmarkList()
    {
        if (_isDisposed || _bookmarkFilter == null || BookmarksListView == null || BookmarkSearchBox == null) return;
        string query = BookmarkSearchBox.Text.Trim();
        var matches = _bookmarkFilter.Filter(SettingsService.Instance.CurrentBookmarks, query);
        if (ReferenceEquals(BookmarksListView.ItemsSource, matches)) return;
        BookmarksListView.ItemsSource = matches;
        BookmarksEmptyText.Text = query.Length == 0 ? "还没有收藏的网页" : "没有找到匹配的收藏";
        UiMotion.SetVisible(BookmarksEmptyText, matches.Count == 0, MotionCue.Fade);
    }

    private void BookmarkItem_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: BookmarkItem item } target ||
            !args.GetCurrentPoint(target).Properties.IsMiddleButtonPressed) return;
        args.Handled = true;
        NewTabRequested?.Invoke(item.Url, !IsKeyPressed(VirtualKey.Shift));
        BookmarksFlyout.Hide();
    }

    private void BookmarkOptions_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: BookmarkItem item } target) return;
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "在新标签页打开" };
        open.Click += (_, _) => { BookmarksFlyout.Hide(); NewTabRequested?.Invoke(item.Url, false); };
        var rename = new MenuFlyoutItem { Text = "重命名" };
        rename.Click += (_, _) => { _bookmarkToRename = item; BookmarksFlyout.Hide(); };
        var remove = new MenuFlyoutItem { Text = "删除收藏", Tag = item };
        remove.Click += BookmarkDelete_Click;
        menu.Items.Add(open); menu.Items.Add(rename); menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(remove);
        menu.ShowAt(target);
    }

    private async void BookmarksFlyout_Closed(object sender, object args)
    {
        ReleaseBookmarkList();
        var item = _bookmarkToRename;
        _bookmarkToRename = null;
        var session = SettingsService.Instance.ActiveBookmarkSession;
        if (item == null || session == null || _isDisposed || !IsLoaded || !VaultService.Instance.IsUnlocked || _bookmarkDialog != null) return;
        var name = new TextBox { Header = "名称", Text = item.Title, MaxLength = 200, MinWidth = 280 };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(name);
        content.Children.Add(new TextBlock { Text = item.Url, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "重命名收藏", Content = content,
            PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text) };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
        dialog.Opened += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
        _bookmarkDialog = dialog;
        try
        {
            if (await WorkspaceDialogs.ShowAsync(dialog) == ContentDialogResult.Primary && !_isDisposed && VaultService.Instance.IsUnlocked &&
                ReferenceEquals(SettingsService.Instance.ActiveBookmarkSession, session))
            {
                string title = name.Text;
                await SaveBookmarkEditAsync(session, current => SettingsService.Instance.RenameBookmark(item.Url, title, current));
            }
        }
        catch (Exception ex) { AppLog.Warn("Rename bookmark: " + ex.GetType().Name); }
        finally { _bookmarkDialog = null; }
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
        bool disabledWhileSaving = _savingBookmark && !BookmarkStarBtn.IsEnabled;
        ToggleBookmarkFromShortcut();
        await WaitForSaveAsync();
        var saved = SettingsService.Instance.CurrentBookmarks.Single(b => b.Url == url);
        if (!disabledWhileSaving || !BookmarkStarBtn.IsEnabled || !await Task.Run(() => new SettingsService().IsBookmarked(url)))
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
        _bookmarkToRename = null;
        _bookmarkDialog?.Hide();
        BookmarksFlyout.Hide();
        ReleaseBookmarkList();
    }

    private void ReleaseBookmarkList()
    {
        _bookmarkFilter = null;
        if (BookmarksListView != null) BookmarksListView.ItemsSource = null;
        if (BookmarkSearchBox != null) BookmarkSearchBox.Text = "";
    }
}
