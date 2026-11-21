using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using StealthWorkspace.Services;
using Windows.System;

namespace StealthWorkspace.Controls;

public sealed record BookmarkManagerEntry(object Value, string Title, string ParentPath)
{
    public string Glyph => Value is BookmarkFolder ? "\uE8B7" : "\uE71B";
    public string Hint => Value is BookmarkItem { ContainsLoginInfo: false } b ? b.Url : Title;
    public Visibility FolderVisibility => Value is BookmarkFolder ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LinkActionVisibility => Value is BookmarkItem { ContainsLoginInfo: false } ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => ParentPath.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public override string ToString() => Title;
}

public sealed partial class BookmarkManager : UserControl
{
    internal sealed record Destination(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly BookmarkFilter _filter = new();
    private SettingsService.BookmarkSession? _session;
    private BookmarkLibrary? _shown;
    private string _current = "";
    private object? _editing;
    private bool _deletePending;
    private bool _dismissFeedbackOnNavigation;
    private BookmarkLibrary? _confirmedDeletion;
    internal string CurrentFolderId => _current;
    internal Action? ClosePane { get; set; }
    internal Action? SaveCurrentPage { get; set; }
    internal Func<Task>? Export { get; set; }
    internal Func<Task>? Import { get; set; }
    internal Action<string>? OpenUrl { get; set; }
    internal Action<string, bool>? OpenNewTab { get; set; }

    public BookmarkManager() => InitializeComponent();

    internal void Open(SettingsService.BookmarkSession session, string folder)
    {
        _session = session;
        _current = folder;
        MoreActions.Visibility = session.Temporary ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }

    internal void RefreshLibrary()
    {
        if (!SessionActive || Equals(_shown, _settings.GetBookmarkLibrary())) return;
        // Keep the user's unfinished edit. Saving still verifies its original item and destination.
        if (!Editing) Refresh(retainPosition: true);
    }

    internal void SyncFolderFromSession()
    {
        if (!SessionActive || Editing || _current == _session!.RailFolderId) return;
        DismissSuccess();
        _current = _session.RailFolderId;
        if (Filter.Text.Trim().Length == 0) Refresh();
    }

    internal void EditPage(BookmarkItem? existing, string title, string url)
    {
        if (!SessionActive) return;
        BeginEdit(existing, addLink: true);
        if (existing == null)
        {
            EntryName.Text = title;
            Address.Text = url;
            EntryName.SelectAll();
        }
    }

    internal void SetCurrentPageStatus(bool enabled, string action)
    {
        CurrentPage.IsEnabled = enabled;
        CurrentLabel.Text = enabled ? action : "收藏本页";
        AutomationProperties.SetName(CurrentPage, action);
        ToolTipService.SetToolTip(CurrentPage, action);
    }

    internal void FocusSearch() => (Editing ? EntryName : Filter).Focus(FocusState.Programmatic);
    internal void ShowImportResult(string message, bool success = false) => Message(message, success);

    private bool SessionActive => _session != null && ReferenceEquals(_session, _settings.ActiveBookmarkSession);
    private bool Editing => Editor.Visibility == Visibility.Visible;
    private void CurrentPage_Click(object sender, RoutedEventArgs args) { if (SessionActive) SaveCurrentPage?.Invoke(); }
    private void Close_Click(object sender, RoutedEventArgs args) => ClosePane?.Invoke();
    private async void Import_Click(object sender, RoutedEventArgs args) { if (SessionActive && Import != null) await Import(); }
    private async void Export_Click(object sender, RoutedEventArgs args) { if (SessionActive && Export != null) await Export(); }

    private void Refresh(bool retainPosition = false)
    {
        if (!SessionActive) return;
        int firstVisible = retainPosition && Rows.ItemsPanelRoot is ItemsStackPanel panel ? panel.FirstVisibleIndex : -1;
        object? top = firstVisible >= 0 && firstVisible < Rows.Items.Count ? (Rows.Items[firstVisible] as BookmarkManagerEntry)?.Value : null;
        var library = _settings.GetBookmarkLibrary();
        _shown = library;
        var folder = _current.Length == 0 ? null : library.Folders.Find(f => f.Id == _current);
        if (folder == null && _current.Length > 0)
        {
            if (_session!.RailFolderId == _current) _session.RailFolderId = "";
            _current = "";
        }
        string query = Filter.Text.Trim();
        bool searching = query.Length != 0;
        Up.Visibility = searching || folder != null ? Visibility.Visible : Visibility.Collapsed;
        Location.Text = searching ? "搜索结果" : folder?.Name ?? "全部收藏";
        ToolTipService.SetToolTip(Location, searching ? "搜索全部文件夹" : library.PathName(_current));
        IEnumerable<object> entries = searching
            ? library.Folders.Where(f => f.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Cast<object>()
                .Concat(_filter.Filter(library.Items, query))
            : library.Children(_current);
        var paths = new Dictionary<string, string>();
        string ParentPath(string parentId)
        {
            if (!searching) return "";
            if (!paths.TryGetValue(parentId, out var path)) paths[parentId] = path = library.PathName(parentId);
            return path;
        }
        var rows = entries.Select(row =>
        {
            if (row is BookmarkFolder child) return new BookmarkManagerEntry(child, child.Name, ParentPath(child.ParentId));
            var link = (BookmarkItem)row;
            string title = link.ContainsLoginInfo ? link.DisplayHost : link.ToString();
            return new BookmarkManagerEntry(link, title, ParentPath(link.FolderId));
        }).ToList();
        Rows.ItemsSource = rows;
        if (firstVisible > 0 && rows.Count > 0)
        {
            var anchor = rows.Find(row => Equals(row.Value, top)) ?? rows[Math.Min(firstVisible, rows.Count - 1)];
            Rows.ScrollIntoView(anchor, ScrollIntoViewAlignment.Leading);
        }
        Count.Text = rows.Count.ToString("N0");
        Empty.Text = searching ? "没有找到匹配的收藏" : folder == null ? "还没有收藏" : "这个文件夹还没有收藏";
        EmptyImport.Visibility = !searching && folder == null && _session!.Temporary == false ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = rows.Count == 0 && !Editing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Up_Click(object sender, RoutedEventArgs args) => GoUp();
    private void GoUp()
    {
        if (!SessionActive) return;
        DismissSuccess();
        if (Filter.Text.Length != 0) { Filter.Text = ""; FocusSearch(); return; }
        if (_current.Length == 0) return;
        var leaving = _current;
        _session!.RailFolderId = _current = _settings.GetBookmarkLibrary().Folders.Find(f => f.Id == leaving)?.ParentId ?? "";
        Refresh();
        var previous = Rows.Items.OfType<BookmarkManagerEntry>().FirstOrDefault(x => x.Value is BookmarkFolder f && f.Id == leaving);
        if (previous != null) FocusRow(previous);
    }

    private void FocusRow(BookmarkManagerEntry entry)
    {
        Rows.SelectedItem = entry;
        Rows.ScrollIntoView(entry);
        Rows.UpdateLayout();
        if (Rows.ContainerFromItem(entry) is ListViewItem container) container.Focus(FocusState.Programmatic);
    }

    private void Filter_Changed(object sender, TextChangedEventArgs args)
    {
        if (SessionActive && !Editing)
        {
            DismissSuccess();
            Refresh();
        }
    }

    private void Row_Click(object sender, ItemClickEventArgs args)
    {
        if (!SessionActive || args.ClickedItem is not BookmarkManagerEntry row) return;
        DismissSuccess();
        if (row.Value is BookmarkFolder folder)
        {
            _session!.RailFolderId = _current = folder.Id;
            Filter.Text = "";
            Refresh();
            if (Rows.Items.Count > 0 && Rows.Items[0] is BookmarkManagerEntry first) FocusRow(first);
            else Up.Focus(FocusState.Programmatic);
        }
        else if (row.Value is BookmarkItem { ContainsLoginInfo: false } link)
        {
            if (Down(VirtualKey.Control)) OpenNewTab?.Invoke(link.Url, !Down(VirtualKey.Shift));
            else OpenUrl?.Invoke(link.Url);
        }
        else BeginEdit(row.Value);
    }

    private static bool Down(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void Row_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!SessionActive || sender is not Grid { Tag: BookmarkManagerEntry { Value: BookmarkItem { ContainsLoginInfo: false } link } } element ||
            !args.GetCurrentPoint(element).Properties.IsMiddleButtonPressed) return;
        args.Handled = true;
        DismissSuccess();
        OpenNewTab?.Invoke(link.Url, !args.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift));
    }

    private void NewTab_Click(object sender, RoutedEventArgs args)
    {
        if (SessionActive && sender is MenuFlyoutItem { Tag: BookmarkManagerEntry { Value: BookmarkItem { ContainsLoginInfo: false } link } })
        {
            DismissSuccess();
            OpenNewTab?.Invoke(link.Url, false);
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs args)
    {
        if (SessionActive && sender is MenuFlyoutItem { Tag: BookmarkManagerEntry entry }) BeginEdit(entry.Value);
    }

    private void Remove_Click(object sender, RoutedEventArgs args)
    {
        if (SessionActive && sender is MenuFlyoutItem { Tag: BookmarkManagerEntry entry })
        {
            BeginEdit(entry.Value);
            AskDelete();
        }
    }

    private void Rows_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Left || args.Key == VirtualKey.Back)
        {
            if (Up.Visibility != Visibility.Visible) return;
            GoUp();
        }
        else if (args.Key == VirtualKey.F2 && Rows.SelectedItem is BookmarkManagerEntry entry) BeginEdit(entry.Value);
        else if ((args.Key == VirtualKey.Application || (args.Key == VirtualKey.F10 && Down(VirtualKey.Shift))) &&
            Rows.SelectedItem is { } selected && Rows.ContainerFromItem(selected) is ListViewItem { ContentTemplateRoot: Grid { ContextFlyout: MenuFlyout menu } grid }) menu.ShowAt(grid);
        else return;
        args.Handled = true;
    }

    private void Manager_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape) return;
        if (Editing) CancelEdit_Click(sender, args);
        else if (Filter.Text.Length > 0) Filter.Text = "";
        else ClosePane?.Invoke();
        args.Handled = true;
    }

    private void Editor_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || args.OriginalSource is not TextBox) return;
        args.Handled = true;
        Save_Click(sender, args);
    }

    private void Create_Click(object sender, RoutedEventArgs args) => BeginEdit(null);
    private void AddLink_Click(object sender, RoutedEventArgs args) => BeginEdit(null, true);

    internal static List<Destination> Destinations(BookmarkLibrary library, string? excluded)
    {
        var rows = library.Folders.Where(f => excluded == null || !library.IsDescendant(f.Id, excluded))
            .Select(f => new Destination(f.Id, library.PathName(f.Id))).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        rows.Insert(0, new("", "全部收藏"));
        return rows;
    }

    private void BeginEdit(object? entry, bool addLink = false)
    {
        if (!SessionActive || !IsEnabled) return;
        _editing = entry;
        _deletePending = false;
        _confirmedDeletion = null;
        Address.Visibility = addLink || entry is BookmarkItem ? Visibility.Visible : Visibility.Collapsed;
        var link = entry as BookmarkItem;
        Address.Text = link is { ContainsLoginInfo: false } ? link.Url : "";
        EntryName.Text = entry is BookmarkFolder f ? f.Name : link is { ContainsLoginInfo: false } ? link.Title : "";
        EditorHeading.Text = entry != null ? "编辑收藏" : addLink ? "添加链接" : "新建文件夹";
        EntryName.IsEnabled = ParentFolder.IsEnabled = entry is not BookmarkFolder { Id: BookmarkFolder.Other };
        string parent = entry is BookmarkFolder folder ? folder.ParentId : link?.FolderId ?? _current;
        var options = Destinations(_settings.GetBookmarkLibrary(), (entry as BookmarkFolder)?.Id);
        ParentFolder.ItemsSource = options;
        ParentFolder.SelectedItem = options.FirstOrDefault(x => x.Id == parent) ?? options[0];
        Editor.Visibility = EditorScroll.Visibility = Visibility.Visible;
        Rows.Visibility = EmptyState.Visibility = DirectoryHeader.Visibility = Actions.Visibility = Visibility.Collapsed;
        SearchRow.Visibility = Visibility.Collapsed;
        Feedback.Visibility = Visibility.Collapsed;
        Delete.Visibility = entry == null ? Visibility.Collapsed : Visibility.Visible;
        Delete.Content = "删除";
        if (link?.ContainsLoginInfo == true) Message("旧网址含登录信息。可以移除此项，或输入不含登录信息的新网址。");
        EntryName.Focus(FocusState.Programmatic);
        EntryName.SelectAll();
    }

    private async void Save_Click(object sender, RoutedEventArgs args)
    {
        if (!SessionActive || _session is not { } session || ParentFolder.SelectedItem is not Destination parent || !IsEnabled) return;
        string name = EntryName.Text.Trim();
        bool link = Address.Visibility == Visibility.Visible;
        string url = Address.Text.Trim();
        if (link)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            { Message("请输入完整的 http 或 https 网址。"); Address.Focus(FocusState.Programmatic); return; }
            if (name.Length == 0) name = uri.Host;
        }
        if (name.Length == 0) { Message("请输入名称。"); return; }
        var entry = _editing;
        await ChangeAsync(() => link ? _settings.EditBookmark(session, entry as BookmarkItem, name, url, parent.Id)
            : _settings.EditBookmarkFolder(session, (entry as BookmarkFolder)?.Id, parent.Id, name, entry as BookmarkFolder), "已保存");
    }

    private void AskDelete()
    {
        _deletePending = true;
        _confirmedDeletion = _settings.GetBookmarkLibrary();
        Delete.Content = "确认删除";
        Message(_editing is BookmarkFolder ? "将删除文件夹和其中的全部收藏。" : "将删除这条收藏。");
        Delete.Focus(FocusState.Programmatic);
    }

    private async void Delete_Click(object sender, RoutedEventArgs args)
    {
        if (!SessionActive || _editing == null || _session is not { } session || !IsEnabled) return;
        if (!_deletePending) { AskDelete(); return; }
        var entry = _editing;
        var confirmed = _confirmedDeletion;
        await ChangeAsync(() => _settings.RemoveBookmarkEntry(session, entry, confirmed), "已删除");
    }

    private async Task ChangeAsync(Func<bool> change, string success)
    {
        IsEnabled = false;
        try
        {
            if (!await Task.Run(change))
            {
                if (!SessionActive) return;
                if (_deletePending)
                {
                    _deletePending = false;
                    Delete.Content = "删除";
                    Message("未能删除。请重新查看文件夹中的收藏后再试。");
                }
                else Message("未能保存。文件夹可能已变更，或同一位置已有这项收藏。");
                return;
            }
            if (!SessionActive) return;
            EndEdit();
            Message(success, success: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("编辑收藏未完成: " + ex.GetType().Name);
            if (SessionActive) Message("写入失败，请检查可用空间后重试。");
        }
        finally { IsEnabled = true; }
    }

    private void Message(string value, bool success = false)
    {
        _dismissFeedbackOnNavigation = success;
        Feedback.Text = value;
        Feedback.Visibility = Visibility.Visible;
    }

    private void DismissSuccess()
    {
        if (!_dismissFeedbackOnNavigation) return;
        _dismissFeedbackOnNavigation = false;
        Feedback.Visibility = Visibility.Collapsed;
    }
    private void EndEdit()
    {
        Editor.Visibility = EditorScroll.Visibility = Feedback.Visibility = Visibility.Collapsed;
        Rows.Visibility = DirectoryHeader.Visibility = Actions.Visibility = SearchRow.Visibility = Visibility.Visible;
        _editing = null;
        _deletePending = false;
        _confirmedDeletion = null;
        EntryName.Text = Address.Text = "";
        ParentFolder.ItemsSource = null;
        if (SessionActive && _current != _session!.RailFolderId)
        {
            _current = _session.RailFolderId;
            Refresh();
        }
        else if (!Equals(_shown, _settings.GetBookmarkLibrary())) Refresh(retainPosition: true);
        else EmptyState.Visibility = Rows.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs args) { EndEdit(); Rows.Focus(FocusState.Programmatic); }
}
