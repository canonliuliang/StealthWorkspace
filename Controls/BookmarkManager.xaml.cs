using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed record BookmarkManagerEntry(object Value, string Title, string Detail, string Glyph)
{
    public string ActionName => "编辑 " + Title;
}

public sealed partial class BookmarkManager : UserControl
{
    internal sealed record Destination(string Id, string Name)
    {
        public override string ToString() => Name;
    }
    private readonly SettingsService _settings = SettingsService.Instance;
    private SettingsService.BookmarkSession? _session;
    private string _current = "";
    private object? _editing;
    private bool _deletePending;
    internal Action? ClosePane { get; set; }
    internal Func<Task>? Import { get; set; }
    internal void FocusSearch() => Filter.Focus(FocusState.Programmatic);
    private void Close_Click(object sender, RoutedEventArgs args) => ClosePane?.Invoke();
    private async void Import_Click(object sender, RoutedEventArgs args) { if (Import != null) await Import(); }
    private void AddLink_Click(object sender, RoutedEventArgs args) => BeginEdit(null, true);
    internal Action<string>? OpenUrl { get; set; }
    internal void ShowImportResult(string message) => Message(message);

    public BookmarkManager() => InitializeComponent();

    internal void Open(SettingsService.BookmarkSession session, string folder)
    {
        _session = session;
        _current = folder;
        ImportButton.Visibility = session.Temporary ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }

    private void Refresh()
    {
        if (_session == null || !ReferenceEquals(_session, _settings.ActiveBookmarkSession)) return;
        var library = _settings.GetBookmarkLibrary();
        if (!library.Folders.Any(f => f.Id == _current)) _current = "";
        Location.Text = library.PathName(_current);
        ToolTipService.SetToolTip(Location, Location.Text);
        Up.IsEnabled = _current.Length != 0 || Filter.Text.Length != 0;
        var query = Filter.Text.Trim();
        IEnumerable<object> entries = query == "" ? library.Children(_current) : library.Folders.Cast<object>()
            .Concat(library.Items).Where(row => row is BookmarkFolder f ? Has(f.Name) : row is BookmarkItem b && (Has(b.Title) || Has(b.Url)));
        bool Has(string value) => value.Contains(query, StringComparison.OrdinalIgnoreCase);
        var rows = entries.Select(row => row is BookmarkFolder f
            ? new BookmarkManagerEntry(f, f.Name, query.Length == 0 ? "文件夹" : library.PathName(f.ParentId), "\uE8B7")
            : new BookmarkManagerEntry(row, ((BookmarkItem)row).Title, ((BookmarkItem)row).Url, "\uE734")).ToList();
        Rows.ItemsSource = rows;
        Empty.Text = query.Length == 0 ? "这个文件夹还没有收藏" : "没有找到匹配的收藏或文件夹";
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Up_Click(object sender, RoutedEventArgs args)
    {
        if (Filter.Text.Length != 0) Filter.Text = "";
        else _current = _settings.GetBookmarkLibrary().Folders.Find(f => f.Id == _current)?.ParentId ?? "";
        EndEdit();
        Refresh();
    }
    private void Filter_Changed(object sender, TextChangedEventArgs args) { if (_session != null) { EndEdit(); Refresh(); } }
    private void Row_Click(object sender, ItemClickEventArgs args)
    {
        if (!ReferenceEquals(_session, _settings.ActiveBookmarkSession) || args.ClickedItem is not BookmarkManagerEntry row) return;
        EndEdit();
        if (row.Value is BookmarkFolder f) { _current = f.Id; Filter.Text = ""; Refresh(); }
        else OpenUrl?.Invoke(((BookmarkItem)row.Value).Url);
    }
    private void Edit_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button {Tag: BookmarkManagerEntry entry}) BeginEdit(entry.Value);
    }
    private void Create_Click(object sender, RoutedEventArgs args) => BeginEdit(null);
    internal static List<Destination> Destinations(BookmarkLibrary library, string? excluded)
    {
        var rows = library.Folders.Where(f => excluded == null || !library.IsDescendant(f.Id, excluded))
            .Select(f => new Destination(f.Id, library.PathName(f.Id))).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        rows.Insert(0, new("", "收藏栏"));
        return rows;
    }
    private void BeginEdit(object? entry, bool addLink = false)
    {
        _editing = entry;
        Address.Visibility = addLink || entry is BookmarkItem ? Visibility.Visible : Visibility.Collapsed;
        Address.Text = (entry as BookmarkItem)?.Url ?? "";
        _deletePending = false;
        EntryName.Text = entry is BookmarkFolder f ? f.Name : (entry as BookmarkItem)?.Title ?? "";
        EntryName.IsEnabled = ParentFolder.IsEnabled = entry is not BookmarkFolder { Id: BookmarkFolder.Other };
        string parent = entry is BookmarkFolder folder ? folder.ParentId : (entry as BookmarkItem)?.FolderId ?? _current;
        var options = Destinations(_settings.GetBookmarkLibrary(), (entry as BookmarkFolder)?.Id);
        ParentFolder.ItemsSource = options;
        ParentFolder.SelectedItem = options.FirstOrDefault(x => x.Id == parent) ?? options[0];
        Editor.Visibility = Visibility.Visible;
        Feedback.Visibility = Visibility.Collapsed;
        Delete.Visibility = entry == null ? Visibility.Collapsed : Visibility.Visible;
        Delete.Content = "删除";
        EntryName.Focus(FocusState.Programmatic);
        EntryName.SelectAll();
    }
    private async void Save_Click(object sender, RoutedEventArgs args)
    {
        if (_session is not { } session || ParentFolder.SelectedItem is not Destination parent) return;
        string name = EntryName.Text.Trim();
        string url = Address.Text.Trim();
        if (Address.Visibility == Visibility.Visible)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            { Message("请输入完整的 http 或 https 网址。"); Address.Focus(FocusState.Programmatic); return; }
            if (name.Length == 0) name = uri.Host;
        }
        if (name.Length == 0) { Message("请输入名称。"); return; }
        var entry = _editing;
        bool link = Address.Visibility == Visibility.Visible;
        await ChangeAsync(() => link ? _settings.EditBookmark(session, entry as BookmarkItem, name, url, parent.Id)
            : _settings.EditBookmarkFolder(session, (entry as BookmarkFolder)?.Id, parent.Id, name), "已保存");
    }
    private async void Delete_Click(object sender, RoutedEventArgs args)
    {
        if (_editing == null || _session is not { } session) return;
        if (!_deletePending)
        {
            _deletePending = true;
            Delete.Content = "确认删除";
            Message(_editing is BookmarkFolder ? "删除后，此文件夹和里面的收藏都将移除。" : "再次确认以删除这条收藏。");
            return;
        }
        var entry = _editing;
        await ChangeAsync(() => _settings.RemoveBookmarkEntry(session, entry), "已删除");
    }
    private async Task ChangeAsync(Func<bool> change, string success)
    {
        IsEnabled = false;
        try
        {
            bool saved = await Task.Run(change);
            if (!ReferenceEquals(_session, _settings.ActiveBookmarkSession)) return;
            if (!saved) { Message("未能保存。请检查同一文件夹中是否已有同名文件夹或相同的网址。"); return; }
            EndEdit();
            Refresh();
            Message(success);
        }
        finally { IsEnabled = true; }
    }
    private void Message(string value) { Feedback.Text = value; Feedback.Visibility = Visibility.Visible; }
    private void EndEdit() { Editor.Visibility = Visibility.Collapsed; Feedback.Visibility = Visibility.Collapsed; _editing = null; _deletePending = false; }
    private void CancelEdit_Click(object sender, RoutedEventArgs args) => EndEdit();
}
