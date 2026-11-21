using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private BookmarkLibrary? _barSnapshot;
    private CancellationTokenSource? _bookmarkImport;

    private void BookmarkBar_Loaded(object sender, RoutedEventArgs args) => RefreshBookmarkBar();

    private void RefreshBookmarkBar()
    {
        if (_isDisposed || BookmarkBarEntries == null) return;
        var settings = SettingsService.Instance;
        var library = settings.GetBookmarkLibrary();
        if (Equals(_barSnapshot, library)) return;
        _barSnapshot = library;
        BookmarkBarEntries.Children.Clear();
        var session = settings.ActiveBookmarkSession;
        var entries = library.Children("").Take(12).ToArray();
        BookmarkBar.Visibility = session != null && entries.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (session == null) return;
        foreach (object row in entries)
        {
            var folder = row as BookmarkFolder;
            var link = row as BookmarkItem;
            string title = folder?.Name ?? link!.Title;
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            label.Children.Add(new FontIcon { Glyph = folder != null ? "\uE8B7" : "\uE734", FontSize = 13 });
            label.Children.Add(new TextBlock { Text = title, MaxWidth = 136, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 });
            var button = new Button { Content = label, Padding = new Thickness(6,2,6,2), MinHeight = 28 };
            button.Style = FavoritesBtn.Style;
            AutomationProperties.SetName(button, folder == null ? title : "打开文件夹 " + title);
            ToolTipService.SetToolTip(button, link?.Url ?? title);
            if (folder != null)
            {
                var menu = new MenuFlyout();
                menu.Opening += (_, _) =>
                {
                    menu.Items.Clear();
                    if (!ReferenceEquals(session, settings.ActiveBookmarkSession)) return;
                    FillBookmarkMenu(menu.Items, folder, settings.GetBookmarkLibrary(), session);
                };
                button.Flyout = menu;
            }
            else button.Click += (_, _) => { if (ReferenceEquals(session, settings.ActiveBookmarkSession)) NavigateToText(link!.Url); };
            BookmarkBarEntries.Children.Add(button);
        }
    }

    private void FillBookmarkMenu(IList<MenuFlyoutItemBase> target, BookmarkFolder folder, BookmarkLibrary library,
        SettingsService.BookmarkSession session)
    {
        var entries = library.Children(folder.Id).Take(21).ToArray();
        foreach (var row in entries.Take(20))
        {
            if (row is BookmarkFolder child)
            {
                var sub = new MenuFlyoutSubItem { Text = child.Name, Icon = new FontIcon { Glyph = "\uE8B7" } };
                // Only materialize a submenu when its parent presenter realizes the row.
                // Keyboard, touch and UI Automation can all expand this native submenu.
                sub.Items.Add(new MenuFlyoutItem { Text = "打开文件夹", IsEnabled = false });
                bool hydrated = false;
                void Hydrate()
                {
                    if (hydrated || _isDisposed || !ReferenceEquals(session, SettingsService.Instance.ActiveBookmarkSession)) return;
                    hydrated = true;
                    sub.Items.Clear();
                    FillBookmarkMenu(sub.Items, child, library, session);
                }
                sub.Loaded += (_, _) => Hydrate();
                sub.GotFocus += (_, _) => Hydrate();
                sub.PointerEntered += (_, _) => Hydrate();
                target.Add(sub);
            }
            else if (row is BookmarkItem b)
            {
                var link = new MenuFlyoutItem { Text = b.Title };
                ToolTipService.SetToolTip(link, b.Url);
                link.Click += (_, _) => { if (!_isDisposed && ReferenceEquals(session, SettingsService.Instance.ActiveBookmarkSession)) NavigateToText(b.Url); };
                target.Add(link);
            }
        }
        if (target.Count != 0) target.Add(new MenuFlyoutSeparator());
        var manage = new MenuFlyoutItem { Text = entries.Length > 20 ? "查看此文件夹的全部收藏" : "管理此文件夹" };
        manage.Click += async (_, _) => { if (ReferenceEquals(session, SettingsService.Instance.ActiveBookmarkSession)) await ShowBookmarkManagerAsync(folder.Id); };
        target.Add(manage);
    }

    private async void ManageBookmarks_Click(object sender, RoutedEventArgs args) => await ShowBookmarkManagerAsync("");
    private async void ImportBrowserBookmarks_Click(object sender, RoutedEventArgs args) => await ImportBrowserBookmarksAsync();

    private async Task ShowBookmarkManagerAsync(string folder, string? message = null)
    {
        var settings = SettingsService.Instance;
        if (_isDisposed || !IsLoaded || _bookmarkDialog != null || settings.ActiveBookmarkSession is not { } session) return;
        BookmarksFlyout.Hide();
        var manager = new BookmarkManager();
        manager.Open(session, folder);
        if (message != null) manager.ShowImportResult(message);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "管理收藏", Content = manager,
            PrimaryButtonText = session.Temporary ? "" : "从 Chrome 导入", CloseButtonText = "完成" };
        string? open = null;
        manager.OpenUrl = url => { open = url; dialog.Hide(); };
        _bookmarkDialog = dialog;
        ContentDialogResult result;
        try { result = await WorkspaceDialogs.ShowAsync(dialog); }
        finally { manager.OpenUrl = null; _bookmarkDialog = null; }
        if (_isDisposed || !IsLoaded || !ReferenceEquals(settings.ActiveBookmarkSession, session)) return;
        if (open != null && MainWindow.Current?.IsPrivacyCoverVisible != true) NavigateToText(open);
        else if (result == ContentDialogResult.Primary) await ImportBrowserBookmarksAsync();
    }

    private async Task ImportBrowserBookmarksAsync()
    {
        var settings = SettingsService.Instance;
        if (_isDisposed || _bookmarkImport != null || _bookmarkDialog != null || settings.PersonalBookmarkSession is not { } session || MainWindow.Current == null) return;
        using var lifetime = new CancellationTokenSource();
        _bookmarkImport = lifetime;
        string? message = null;
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
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            message = ex is InvalidDataException ? ex.Message : "导入未完成。请选择 Chrome 书签管理器导出的 HTML 文件，并确认文件可以读取。";
            AppLog.Warn("浏览器收藏导入未完成: " + ex.GetType().Name);
        }
        finally { _bookmarkImport = null; }
        if (message != null && !lifetime.IsCancellationRequested && ReferenceEquals(settings.PersonalBookmarkSession,session))
            await ShowBookmarkManagerAsync("", message);
    }

    private async Task SaveToBookmarkFolderAsync(string title, Uri address, SettingsService.BookmarkSession session, BookmarkLibrary library)
    {
        if (_bookmarkDialog != null) return;
        var existing = SettingsService.Instance.FirstBookmark(library.Items, address);
        string url = address.AbsoluteUri;
        var name = new TextBox { Header = "名称", Text = existing?.Title ?? title, MaxLength = 200 };
        var folders = BookmarkManager.Destinations(library, null);
        var select = new ComboBox { Header = "保存到", ItemsSource = folders, HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedItem = folders.FirstOrDefault(f => f.Id == existing?.FolderId) ?? folders[0] };
        var form = new StackPanel { Spacing = 10, MinWidth = 250 };
        form.Children.Add(name); form.Children.Add(select);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = existing == null ? "收藏网页" : "编辑收藏", Content = form,
            PrimaryButtonText = "保存", SecondaryButtonText = existing == null ? "" : "移除此收藏", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        _bookmarkDialog = dialog;
        try
        {
            var result = await WorkspaceDialogs.ShowAsync(dialog);
            if (!ReferenceEquals(session, SettingsService.Instance.ActiveBookmarkSession) || _isDisposed) return;
            string newName = name.Text.Trim();
            if (result == ContentDialogResult.Primary && select.SelectedItem is BookmarkManager.Destination d)
                await SaveBookmarkEditAsync(session, current => SettingsService.Instance.EditBookmark(current, existing, newName, url, d.Id));
            else if (result == ContentDialogResult.Secondary && existing != null)
                await SaveBookmarkEditAsync(session, current => SettingsService.Instance.RemoveBookmarkEntry(current, existing));
        }
        finally { _bookmarkDialog = null; }
    }
}
