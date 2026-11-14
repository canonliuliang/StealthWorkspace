using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using StealthWorkspace.Models;
using StealthWorkspace.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace StealthWorkspace.Pages;

public sealed partial class VaultPage : Page
{
    private readonly ObservableRangeCollection<VaultItem> _displayedItems = new();
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _totalsDirty = true;
    private long _totalBytes;
    private bool _loaded;
    private bool _busy;
    private bool _picking;
    private CancellationTokenSource? _importLifetime;
    private readonly List<(string Path, string Reason)> _failedImports = new();
    private CancellationTokenSource? _thumbnailLifetime;
    private readonly SemaphoreSlim _thumbnailSlots = new(2, 2);
    private readonly Dictionary<Image, CancellationTokenSource> _thumbnailLoads = new();
    private readonly HashSet<Image> _thumbnailImages = new();
    private VaultViewState? _restoreState;
    internal IEnumerable<VaultItem> DisplayedItems => _displayedItems;
    internal void FlushPendingSearch()
    {
        if (!_searchDebounce.IsEnabled) return;
        _searchDebounce.Stop();
        ApplyFilterAndSearch();
        if (_loaded) UpdateUIState();
    }
    internal VaultViewState CaptureViewState() => new(SearchBox.Text ?? "", MediaFilter.SelectedIndex, (MediaGridView.SelectedItem as VaultItem)?.Id);
    internal void SetSmokeFilter(string query, int filter)
    {
        if (!SmokeTest.Enabled) throw new InvalidOperationException("Test-only filter");
        SearchBox.Text = query;
        MediaFilter.SelectedIndex = filter;
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _restoreState = e.Parameter as VaultViewState;
        if (_restoreState is { } state)
        {
            SearchBox.Text = state.Search;
            MediaFilter.SelectedIndex = Math.Clamp(state.FilterIndex, 0, 2);
        }
    }

    public VaultPage()
    {
        InitializeComponent();
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            if (!_loaded) return;
            ApplyFilterAndSearch();
            UpdateUIState();
        };
        Loaded += VaultPage_Loaded;
        Unloaded += VaultPage_Unloaded;
    }

    private void VaultPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _totalsDirty = true;
        _thumbnailLifetime = new CancellationTokenSource();

        MediaGridView.ItemsSource = _displayedItems;
        VaultService.Instance.VisibleItems.CollectionChanged += VisibleItems_CollectionChanged;
        VaultService.Instance.Locked += CancelImport;
        ApplyFilterAndSearch();
        UpdateUIState();
        if (_restoreState?.SelectedId is { } selectedId)
        {
            var selected = _displayedItems.FirstOrDefault(item => item.Id == selectedId);
            if (selected != null)
            {
                MediaGridView.SelectedItem = selected;
                MediaGridView.ScrollIntoView(selected);
            }
        }
        _restoreState = null;
    }

    private void VaultPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _searchDebounce.Stop();
        VaultService.Instance.VisibleItems.CollectionChanged -= VisibleItems_CollectionChanged;
        VaultService.Instance.Locked -= CancelImport;
        CancelImport();
        _failedImports.Clear();
        _thumbnailLifetime?.Cancel();
        foreach (Image image in _thumbnailImages.ToArray()) ReleaseThumbnail(image);
        _thumbnailImages.Clear();
        _thumbnailLifetime?.Dispose();
        _thumbnailLifetime = null;
        _loaded = false;
    }

    private async void Thumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image)
        {
            _thumbnailImages.Add(image);
            await LoadThumbnailAsync(image);
        }
    }

    private async void Thumbnail_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs e)
    {
        if (sender is not Image image) return;
        ReleaseThumbnail(image);
        if (image.IsLoaded) await LoadThumbnailAsync(image);
    }

    private void Thumbnail_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image)
        {
            ReleaseThumbnail(image);
            _thumbnailImages.Remove(image);
        }
    }

    private void ReleaseThumbnail(Image image)
    {
        // The async operation disposes its token after cancellation has unwound.
        if (_thumbnailLoads.Remove(image, out var pending)) pending.Cancel();
        var previous = image.Source as IDisposable;
        image.Source = null;
        previous?.Dispose();
    }

    internal async Task<bool> WaitForSmokeThumbnailAsync()
    {
        static bool HasImage(DependencyObject element)
        {
            if (element is Image { Name: "MediaThumbnail", Source: not null }) return true;
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element); i++)
                if (HasImage(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i))) return true;
            return false;
        }

        for (int i = 0; i < 40; i++)
        {
            if (HasImage(MediaGridView)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private async Task LoadThumbnailAsync(Image image)
    {
        if (image.DataContext is not VaultItem { MediaType: "Image" } item ||
            _thumbnailLifetime is not { } lifetime || image.Source != null || _thumbnailLoads.ContainsKey(image)) return;

        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _thumbnailLoads[image] = request;
        CancellationToken token = request.Token;
        try
        {
            await _thumbnailSlots.WaitAsync(token);
            try
            {
                if (token.IsCancellationRequested || !image.IsLoaded || image.Source != null ||
                    !ReferenceEquals(image.DataContext, item) || !VaultService.Instance.IsUnlocked) return;
                var preview = await VaultImageDecoder.DecodeAsync(item, 400, token);
                if (!token.IsCancellationRequested && image.IsLoaded && ReferenceEquals(image.DataContext, item) && VaultService.Instance.IsUnlocked)
                    image.Source = preview.Source;
                else preview.Source.Dispose();
            }
            finally
            {
                _thumbnailSlots.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn($"图片缩略图不可用（0x{ex.HResult:X8}）");
        }
        finally
        {
            if (_thumbnailLoads.TryGetValue(image, out var current) && ReferenceEquals(current, request))
                _thumbnailLoads.Remove(image);
        }
    }

    private void VisibleItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _totalsDirty = true;
        if (!_loaded || _busy) return;
        ApplyFilterAndSearch();
        UpdateUIState();
    }

    private void UpdateUIState()
    {
        // Deliberately identical for the real and decoy spaces: a visible "decoy" badge
        // would instantly expose the ruse to whoever coerced the password.
        VaultBadgeText.Text = "加密保险箱";
        ToolTipService.SetToolTip(VaultBadge, "AES-256-GCM 分块加密 · 播放全程内存解密");

        int totalCount = VaultService.Instance.VisibleItems.Count;
        if (_totalsDirty)
        {
            _totalBytes = VaultService.Instance.VisibleItems.Sum(x => x.FileSizeBytes);
            _totalsDirty = false;
        }

        StatsSummaryText.Text = $"共 {totalCount} 项 · 原始大小 {FormatBytes(_totalBytes)} · 显示 {_displayedItems.Count} 项";
        EmptyNotice.Visibility = _displayedItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitleText.Text = totalCount == 0 ? "你的媒体，留在这里" : "没有匹配的媒体";
        EmptyDescriptionText.Text = totalCount == 0 ? "拖入图片或视频，或点击「导入文件」开始。" : "尝试其他文件名，或将媒体类型切换为「全部媒体」。";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    private void ApplyFilterAndSearch()
    {
        if (!_loaded) return;

        string query = SearchBox?.Text?.Trim() ?? string.Empty;
        bool filterVideo = MediaFilter?.SelectedIndex == 1;
        bool filterImage = MediaFilter?.SelectedIndex == 2;

        var filtered = VaultService.Instance.VisibleItems.Where(item =>
        {
            if (item == null) return false;
            if (query.Length > 0 && !(item.OriginalFileName ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (filterVideo && item.MediaType != "Video") return false;
            if (filterImage && item.MediaType != "Image") return false;
            return true;
        }).ToList();

        var selected = MediaGridView.SelectedItem as VaultItem;
        // Small imports preserve decoded containers; large searches emit one reset.
        _displayedItems.Reconcile(filtered);
        if (selected != null && filtered.Contains(selected)) MediaGridView.SelectedItem = selected;

        if (EmptyNotice != null)
        {
            EmptyNotice.Visibility = _displayedItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _searchDebounce.Stop();
        if (!_loaded) return;
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && VaultService.Instance.VisibleItems.Count > 250)
        {
            _searchDebounce.Start();
            return;
        }
        ApplyFilterAndSearch();
        if (_loaded) UpdateUIState();
    }

    private void MediaFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _searchDebounce.Stop();
        ApplyFilterAndSearch();
        if (_loaded) UpdateUIState();
    }

    #region Import

    private async void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _picking) return;
        _picking = true;

        try
        {
            List<string> selectedPaths;
            try
            {
                var picker = new FileOpenPicker();
                if (MainWindow.CurrentHandle != IntPtr.Zero)
                {
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.CurrentHandle);
                }

                picker.FileTypeFilter.Add("*");
                var files = await picker.PickMultipleFilesAsync();
                if (files == null || files.Count == 0) return;
                selectedPaths = files.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            }
            catch (Exception pickerEx)
            {
                AppLog.Warn($"WinRT 文件选择器失败，改用 Win32（0x{pickerEx.HResult:X8}）");
                selectedPaths = Services.NativeMethods.PickMultipleFilesWin32(MainWindow.CurrentHandle);
            }

            if (selectedPaths.Count == 0) return;
            await ImportFilesAsync(selectedPaths);
        }
        catch (Exception ex)
        {
            AppLog.Error("打开文件选择器失败", ex);
            StatusText.Text = "打开文件选择器失败：" + ex.Message;
        }
        finally { _picking = false; }
    }

    internal async Task<(int Imported, int Failed)> ImportFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || _busy) return (0, 0);
        paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _failedImports.Clear();
        FailureActions.Visibility = Visibility.Collapsed;
        using var importLifetime = new CancellationTokenSource();
        _importLifetime = importLifetime;
        CancellationToken token = importLifetime.Token;

        SetBusy(true);
        int success = 0;
        int failed = 0;
        int originalRetained = 0;
        string? firstFailure = null;
        try
        {
            bool shred = ShredOriginalCheckBox.IsChecked;
            if (shred)
            {
                ShredOriginalCheckBox.IsChecked = false;
                var confirm = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "导入后删除原文件？",
                    Content = "仅在加密导入成功后处理原件。此操作不可撤销。",
                    PrimaryButtonText = "导入并删除原件",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return (0, 0);
            }
            ImportProgressBar.Visibility = Visibility.Visible;
            CancelImportButton.Visibility = Visibility.Visible;
            ImportProgressBar.Value = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                if (token.IsCancellationRequested || !VaultService.Instance.IsUnlocked) break;
                string path = paths[i];
                string name = Path.GetFileName(path);
                StatusText.Text = $"正在加密导入 ({i + 1}/{paths.Count}): {name}";
                ImportProgressBar.Value = (double)i / paths.Count * 100;

                double basePercent = (double)i / paths.Count;
                var progress = new Progress<double>(value =>
                {
                    if (ReferenceEquals(_importLifetime, importLifetime) && !token.IsCancellationRequested)
                        ImportProgressBar.Value = Math.Max(ImportProgressBar.Value, (basePercent + value / paths.Count) * 100);
                });

                try
                {
                    await VaultService.Instance.ImportFileAsync(path, shred, progress, token,
                        reportOriginalRemoval: removed => { if (!removed) originalRetained++; });
                    success++;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    failed++;
                    string detail = DescribeImportFailure(ex);
                    firstFailure ??= detail;
                    _failedImports.Add((path, detail));
                    AppLog.Error($"导入媒体失败 (0x{ex.HResult:X8})", ex);
                    if (SmokeTest.UiSmoke) SmokeTest.Report($"media import failure: {ex.GetType().Name}, 0x{ex.HResult:X8} | {detail}");
                }
            }

            ImportProgressBar.Value = 100;
            StatusText.Text = failed == 0
                ? $"已加密导入 {success} 个文件。"
                : $"导入结果：成功 {success} 个，失败 {failed} 个。{firstFailure}";
            if (token.IsCancellationRequested || !VaultService.Instance.IsUnlocked)
                StatusText.Text = $"导入已停止：已成功 {success} 个，失败 {failed} 个，未完成 {paths.Count - success - failed} 个。已导入的文件仍保留。";
            if (shred && success > 0)
                StatusText.Text += $"已移除 {success - originalRetained} 个原件。" +
                    (originalRetained > 0 ? $"{originalRetained} 个原件未能移除。" : "");
            return (success, failed);
        }
        finally
        {
            ImportProgressBar.Visibility = Visibility.Collapsed;
            CancelImportButton.Visibility = Visibility.Collapsed;
            _importLifetime = null;
            if (!_loaded || !VaultService.Instance.IsUnlocked) _failedImports.Clear();
            FailureActions.Visibility = _failedImports.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SetBusy(false);
            ApplyFilterAndSearch();
            UpdateUIState();
        }
    }

    private void CancelImport() => _importLifetime?.Cancel();
    private void CancelImport_Click(object sender, RoutedEventArgs e) => CancelImport();
    private async void RetryFailed_Click(object sender, RoutedEventArgs e)
    {
        await RetryFailedImportsAsync();
    }

    internal async Task<(int Imported, int Failed)> RetryFailedImportsAsync()
    {
        // A retry never inherits the destructive original-removal option.
        ShredOriginalCheckBox.IsChecked = false;
        return await ImportFilesAsync(_failedImports.Select(f => f.Path).ToArray());
    }
    private async void ImportDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || XamlRoot == null) return;
        var text = new TextBlock { Text = string.Join("\n\n", _failedImports.Select(f => $"{Path.GetFileName(f.Path)}\n{f.Reason}")), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        await new ContentDialog { XamlRoot = XamlRoot, Title = "未导入的文件", Content = new ScrollViewer { Content = text, MaxHeight = 340 }, CloseButtonText = "关闭" }.ShowAsync();
    }

    private static string DescribeImportFailure(Exception error) => error switch
    {
        UnauthorizedAccessException => "没有读取原文件或写入保险箱的权限。",
        FileNotFoundException => "找不到原文件，请确认文件已下载到本机。",
        DirectoryNotFoundException => "原文件或保险箱目录不可用。",
        IOException io when (io.HResult & 0xFFFF) is 39 or 112 => "磁盘空间不足。",
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "文件正在被其他程序使用。",
        IOException => $"读写文件失败（0x{error.HResult:X8}）。",
        OperationCanceledException => "导入已取消。",
        InvalidOperationException => error.Message,
        _ => $"无法加密导入（0x{error.HResult:X8}）。"
    };

    #endregion

    #region Drag & drop

    private void VaultPage_DragOver(object sender, DragEventArgs e)
    {
        if (_busy) return;

        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.Caption = "松开以加密导入";
            DragOverlay.Visibility = Visibility.Visible;
        }
    }

    private void VaultPage_DragLeave(object sender, DragEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;
    }

    private async void VaultPage_Drop(object sender, DragEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;
        if (_busy) return;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.OfType<StorageFile>().Select(f => f.Path)
                .Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count > 0)
            {
                await ImportFilesAsync(paths);
            }
            else StatusText.Text = "请拖入文件；文件夹和尚未下载的虚拟文件暂不支持。";
        }
        catch (Exception ex)
        {
            AppLog.Error("拖拽导入失败", ex);
            StatusText.Text = "拖拽导入失败：" + ex.Message;
        }
        finally { deferral.Complete(); }
    }

    #endregion

    #region Play / export / delete

    private VaultItem? FindItemById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return VaultService.Instance.VisibleItems.FirstOrDefault(i => i.Id == id);
    }

    private void MediaGridView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_busy) return;
        // A double-click on empty space must not reopen a previously selected file.
        if (MediaItemFromSource(e.OriginalSource) is VaultItem item)
        {
            e.Handled = true;
            MainWindow.Current?.NavigateToPlayer(item);
        }
    }

    private VaultItem? MediaItemFromSource(object sourceElement)
    {
        DependencyObject? source = sourceElement as DependencyObject;
        while (source != null && source is not GridViewItem && !ReferenceEquals(source, MediaGridView))
            source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
        return source is GridViewItem container ? MediaGridView.ItemFromContainer(container) as VaultItem : null;
    }

    private void MediaGridView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_busy || e.Key != Windows.System.VirtualKey.Enter || e.KeyStatus.WasKeyDown) return;
        // Ctrl+arrow can move keyboard focus without moving selection.
        var item = MediaItemFromSource(e.OriginalSource) ?? MediaGridView.SelectedItem as VaultItem;
        if (item == null) return;
        e.Handled = true;
        MainWindow.Current?.NavigateToPlayer(item);
    }

    private void MediaGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer,
            args.InRecycleQueue ? string.Empty : (args.Item as VaultItem)?.OriginalFileName ?? string.Empty);
    }

    private void PlaySelected_Click(object sender, RoutedEventArgs e)
    {
        if (MediaGridView.SelectedItem is VaultItem item)
        {
            MainWindow.Current?.NavigateToPlayer(item);
        }
        else
        {
            StatusText.Text = "请先选中要播放的项目";
        }
    }

    private async void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        if (MediaGridView.SelectedItem is VaultItem item)
        {
            await DoExportAsync(item);
        }
        else
        {
            StatusText.Text = "请先选中要导出的项目";
        }
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (MediaGridView.SelectedItem is VaultItem item)
        {
            await DoDeleteAsync(item);
        }
        else
        {
            StatusText.Text = "请先选中要粉碎删除的项目";
        }
    }

    private void FlyoutPlay_Click(object sender, RoutedEventArgs e)
    {
        VaultItem? item = FindItemById((sender as FrameworkElement)?.Tag as string);
        if (item != null) MainWindow.Current?.NavigateToPlayer(item);
    }

    private async void FlyoutExport_Click(object sender, RoutedEventArgs e)
    {
        VaultItem? item = FindItemById((sender as FrameworkElement)?.Tag as string);
        if (item != null) await DoExportAsync(item);
    }

    private async void FlyoutDelete_Click(object sender, RoutedEventArgs e)
    {
        VaultItem? item = FindItemById((sender as FrameworkElement)?.Tag as string);
        if (item != null) await DoDeleteAsync(item);
    }

    private async Task DoExportAsync(VaultItem item)
    {
        if (_busy) return;
        if (!VaultService.Instance.IsUnlocked)
        {
            StatusText.Text = "保险箱已锁定，无法导出。";
            return;
        }

        try
        {
            var savePicker = new FileSavePicker();
            if (MainWindow.CurrentHandle != IntPtr.Zero)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, MainWindow.CurrentHandle);
            }

            savePicker.SuggestedStartLocation = PickerLocationId.Downloads;
            savePicker.SuggestedFileName = string.IsNullOrWhiteSpace(item.OriginalFileName) ? "media" : item.OriginalFileName;
            string ext = string.IsNullOrEmpty(item.FileExtension) ? ".bin" : item.FileExtension;
            savePicker.FileTypeChoices.Add("原媒体格式", new[] { ext });

            var targetFile = await savePicker.PickSaveFileAsync();
            if (targetFile == null) return;

            SetBusy(true);
            ImportProgressBar.Visibility = Visibility.Visible;
            ImportProgressBar.Value = 0;
            StatusText.Text = $"正在解密导出: {targetFile.Name}…";

            var progress = new Progress<double>(value => ImportProgressBar.Value = value * 100);
            try
            {
                await VaultService.Instance.ExportItemAsync(item, targetFile.Path, progress);
                StatusText.Text = $"已导出解密副本: {targetFile.Name}";
            }
            finally
            {
                ImportProgressBar.Visibility = Visibility.Collapsed;
                SetBusy(false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("导出媒体失败", ex);
            StatusText.Text = "导出失败：" + ex.Message;
        }
    }

    private async Task DoDeleteAsync(VaultItem item)
    {
        if (_busy) return;

        if (XamlRoot == null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "彻底粉碎删除？",
            Content = $"将覆盖并删除加密文件：\n{item.OriginalFileName}\n\n此操作不可恢复。",
            PrimaryButtonText = "粉碎删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        try
        {
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            SetBusy(true);
            StatusText.Text = "正在粉碎删除…";
            await VaultService.Instance.DeleteItemAsync(item);
            StatusText.Text = $"已彻底粉碎并清理: {item.OriginalFileName}";
            ApplyFilterAndSearch();
            UpdateUIState();
        }
        catch (Exception ex)
        {
            AppLog.Error("删除媒体失败", ex);
            StatusText.Text = "删除失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ImportButton.IsEnabled = !busy;
        PlayButton.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy;
        DeleteButton.IsEnabled = !busy;
        ShredOriginalCheckBox.IsEnabled = !busy;
    }

    #endregion
}
