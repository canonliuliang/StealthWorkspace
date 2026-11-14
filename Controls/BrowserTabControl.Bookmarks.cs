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

    private void BookmarkSearch_Changed(object sender, TextChangedEventArgs args) => RefreshBookmarkList();

    private void RefreshBookmarkList()
    {
        if (_isDisposed || BookmarksListView == null || BookmarkSearchBox == null) return;
        string query = BookmarkSearchBox.Text.Trim();
        var matches = SettingsService.Instance.Data.Bookmarks.Where(item => query.Length == 0 ||
            item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Url.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        BookmarksListView.ItemsSource = matches;
        BookmarksEmptyText.Text = query.Length == 0 ? "还没有收藏的网页" : "没有找到匹配的收藏";
        BookmarksEmptyText.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        var item = _bookmarkToRename;
        _bookmarkToRename = null;
        if (item == null || _isDisposed || !IsLoaded || _bookmarkDialog != null) return;
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
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !_isDisposed && VaultService.Instance.IsUnlocked &&
                !SettingsService.Instance.RenameBookmark(item.Url, name.Text)) ShowBookmarkSaveError();
        }
        catch (Exception ex) { AppLog.Warn("Rename bookmark: " + ex.GetType().Name); }
        finally { _bookmarkDialog = null; }
    }

    private void ShowBookmarkSaveError() => ShowBrowserError("收藏未保存", "无法写入收藏数据，原来的收藏已保留。请检查磁盘空间或文件占用后重试。");

    private void DetachBookmarkUi()
    {
        _bookmarkToRename = null;
        _bookmarkDialog?.Hide();
        BookmarksFlyout.Hide();
        BookmarksListView.ItemsSource = null;
    }
}
