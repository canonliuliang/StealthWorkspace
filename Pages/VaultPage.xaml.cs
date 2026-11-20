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
    private readonly Dictionary<Image, (CancellationTokenSource Token, TaskCompletionSource Done)> _thumbnailLoads = new();
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
    internal VaultViewState CaptureViewState() => new(SearchBox.Text ?? "", MediaFilter.SelectedIndex,
        (MediaGridView.SelectedItem as VaultItem)?.Id, MediaSort.SelectedIndex);
    internal void SetSmokeFilter(string query, int filter, int? sort = null)
    {
        if (!SmokeTest.Enabled) throw new InvalidOperationException("Test-only filter");
        SearchBox.Text = query;
        MediaFilter.SelectedIndex = filter;
        if (sort.HasValue) MediaSort.SelectedIndex = sort.Value;
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _restoreState = e.Parameter as VaultViewState;
        if (_restoreState is { } state)
        {
            SearchBox.Text = state.Search;
            MediaFilter.SelectedIndex = Math.Clamp(state.FilterIndex, 0, 2);
            MediaSort.SelectedIndex = Math.Clamp(state.SortIndex, 0, 2);
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
        VaultWork.Changed += UpdateSelectionCommands;
        ApplyFilterAndSearch();
        UpdateUIState(animate: false);
        UpdateSelectionCommands();
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
        VaultWork.Changed -= UpdateSelectionCommands;
        _failedImports.Clear();
        _thumbnailLifetime?.Cancel();
        foreach (Image image in _thumbnailImages.ToArray()) ReleaseThumbnail(image);
        _thumbnailImages.Clear();
        _thumbnailLifetime?.Dispose();
        _thumbnailLifetime = null;
        _loaded = false;
    }

    private void ReleaseThumbnail(Image image)
    {
        // The async operation disposes its token after cancellation has unwound.
        if (_thumbnailLoads.Remove(image, out var pending)) pending.Token.Cancel();
        var previous = image.Source as IDisposable;
        image.Source = null;
        image.Tag = null;
        previous?.Dispose();
    }

    internal async Task<bool> WaitForSmokeThumbnailAsync(string? id = null)
    {
        bool HasImage(DependencyObject element)
        {
            if (element is Image { Name: "MediaThumbnail", Source: not null, Tag: VaultItem item }
                && (id == null || id == item.Id)) return true;
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element); i++)
                if (HasImage(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i))) return true;
            return false;
        }

        for (int i = 0; i < 100; i++)
        {
            if (HasImage(MediaGridView)) return true;
            await Task.Delay(100);
        }
        if (SmokeTest.Enabled)
        {
            var cards = _thumbnailImages.Select(image => $"{(image.DataContext as VaultItem)?.MediaType ?? "none"} / {image.IsLoaded} / {image.Source != null}");
            SmokeTest.Report("thumbnail wait expired: " + string.Join(", ", cards) + "; active=" + _thumbnailLoads.Count);
        }
        return false;
    }

    private async Task LoadThumbnailAsync(Image image)
    {
        if (image.Tag is not VaultItem { MediaType: "Image" or "Video" } item ||
            _thumbnailLifetime is not { } lifetime || image.Source != null || _thumbnailLoads.ContainsKey(image)) return;

        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thumbnailLoads[image] = (request, done);
        CancellationToken token = request.Token;
        try
        {
            await _thumbnailSlots.WaitAsync(token);
            try
            {
                if (token.IsCancellationRequested || image.Source != null ||
                    !ReferenceEquals(image.Tag, item) || !VaultService.Instance.IsUnlocked) return;
                Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource preview;
                TimeSpan duration = default;
                if (item.MediaType == "Video")
                {
                    if (SmokeTest.Enabled) SmokeTest.Report("video preview: decoding visible card");
                    (preview, duration) = await VaultVideoPreview.DecodeAsync(item, token);
                }
                else preview = (await VaultImageDecoder.DecodeAsync(item, 400, token)).Source;
                if (!token.IsCancellationRequested && ReferenceEquals(image.Tag, item) && VaultService.Instance.IsUnlocked)
                {
                    item.SetVideoDuration(duration);
                    image.Source = preview;
                    if (SmokeTest.Enabled && item.MediaType == "Video") SmokeTest.Report("video preview: rendered " + item.DisplayDuration);
                    UiMotion.Reveal(image, MotionCue.Fade);
                }
                else preview.Dispose();
            }
            finally
            {
                _thumbnailSlots.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn($"媒体缩略图不可用（0x{ex.HResult:X8}）");
            if (SmokeTest.Enabled) SmokeTest.Report($"thumbnail unavailable: {item.MediaType} / {ex.GetType().Name} / {ex.HResult:X8}");
        }
        finally
        {
            if (_thumbnailLoads.TryGetValue(image, out var current) && ReferenceEquals(current.Token, request))
                _thumbnailLoads.Remove(image);
            done.TrySetResult();
        }
    }

    private void VisibleItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _totalsDirty = true;
        if (!_loaded || _busy) return;
        ApplyFilterAndSearch();
        UpdateUIState();
    }

    private void UpdateUIState(bool animate = true)
    {
        int totalCount = VaultService.Instance.VisibleItems.Count;
        if (_totalsDirty)
        {
            _totalBytes = VaultService.Instance.VisibleItems.Sum(x => x.FileSizeBytes);
            _totalsDirty = false;
        }

        StatsSummaryText.Text = $"共 {totalCount} 项 · 原始大小 {FormatBytes(_totalBytes)} · 显示 {_displayedItems.Count} 项";
        EmptyTitleText.Text = totalCount == 0 ? "你的媒体，留在这里" : "没有匹配的媒体";
        EmptyDescriptionText.Text = totalCount == 0 ? "拖入图片或视频，或点击「导入文件」开始。" : "尝试其他文件名，或将媒体类型切换为「全部媒体」。";
        if (animate)
            UiMotion.SetVisible(EmptyNotice, _displayedItems.Count == 0, MotionCue.Fade);
        else
            EmptyNotice.Visibility = _displayedItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    private bool ApplyFilterAndSearch()
    {
        if (!_loaded) return false;

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
        });

        var sorted = (MediaSort.SelectedIndex switch
        {
            1 => filtered.OrderBy(item => item.OriginalFileName, StringComparer.CurrentCultureIgnoreCase),
            2 => filtered.OrderByDescending(item => item.FileSizeBytes),
            _ => filtered.OrderByDescending(item => item.ImportedAt)
        }).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var selected = MediaGridView.SelectedItems.OfType<VaultItem>().ToHashSet();
        // Small imports preserve decoded containers; large searches emit one reset.
        bool changed = _displayedItems.Reconcile(sorted);
        if (changed && selected.Count != 0)
        {
            foreach (var item in sorted.Where(selected.Contains))
            {
                if (MediaGridView.SelectionMode == ListViewSelectionMode.Single) MediaGridView.SelectedItem = item;
                else if (!MediaGridView.SelectedItems.Contains(item)) MediaGridView.SelectedItems.Add(item);
            }
        }
        return changed;
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
        bool changed = ApplyFilterAndSearch();
        if (_loaded) UpdateUIState();
        if (changed && _displayedItems.Count > 0) UiMotion.Reveal(MediaGridView, MotionCue.Fade);
    }

    #region Import

    private async void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || VaultWork.IsBusy || _picking) return;
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
                var files = await WorkspaceNativeDialogs.PickAsync(picker.PickMultipleFilesAsync);
                if (files == null || files.Count == 0) return;
                selectedPaths = files.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            }
            catch (Exception pickerEx)
            {
                AppLog.Warn($"WinRT 文件选择器失败，改用 Win32（0x{pickerEx.HResult:X8}）");
                selectedPaths = WorkspaceNativeDialogs.PickWin32(() => Services.NativeMethods.PickMultipleFilesWin32(MainWindow.CurrentHandle)) ?? new();
            }

            if (selectedPaths.Count == 0 || !_loaded || !VaultService.Instance.IsUnlocked) return;
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
        if (paths.Count == 0 || _busy || !_loaded || !VaultService.Instance.IsUnlocked) return (0, 0);
        using var work = VaultWork.TryBegin("正在加密导入");
        if (work == null) return (0, 0);
        paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _failedImports.Clear();
        UiMotion.SetVisible(FailureActions, false);
        using var importLifetime = CancellationTokenSource.CreateLinkedTokenSource(work.Token);
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
                if (await WorkspaceDialogs.ShowAsync(confirm) != ContentDialogResult.Primary || !_loaded || token.IsCancellationRequested)
                    return (0, 0);
            }
            CancelImportButton.Visibility = Visibility.Visible;
            ImportProgressBar.Value = 0;
            UiMotion.SetVisible(OperationProgress, true, MotionCue.FromBelow);
            for (int i = 0; i < paths.Count; i++)
            {
                if (token.IsCancellationRequested || !VaultService.Instance.IsUnlocked) break;
                string path = paths[i];
                string name = Path.GetFileName(path);
                if (_loaded)
                {
                    StatusText.Text = $"正在加密导入 ({i + 1}/{paths.Count}) · {name}";
                    ImportProgressBar.Value = (double)i / paths.Count * 100;
                }

                double basePercent = (double)i / paths.Count;
                var progress = new Progress<double>(value =>
                {
                    if (!ReferenceEquals(_importLifetime, importLifetime) || token.IsCancellationRequested) return;
                    double fraction = basePercent + value / paths.Count;
                    work.Report(fraction);
                    if (_loaded) ImportProgressBar.Value = Math.Max(ImportProgressBar.Value, fraction * 100);
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
                    if (_loaded) _failedImports.Add((path, detail));
                    AppLog.Error($"导入媒体失败 (0x{ex.HResult:X8})", ex);
                    if (SmokeTest.UiSmoke) SmokeTest.Report($"media import failure: {ex.GetType().Name}, 0x{ex.HResult:X8} | {detail}");
                }
            }

            if (!_loaded) return (success, failed);
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
            _importLifetime = null;
            if (!_loaded || !VaultService.Instance.IsUnlocked) _failedImports.Clear();
            if (_loaded)
            {
                UiMotion.SetVisible(OperationProgress, false);
                CancelImportButton.Visibility = Visibility.Collapsed;
                UiMotion.SetVisible(FailureActions, _failedImports.Count > 0, MotionCue.FromBelow);
            }
            SetBusy(false);
            if (_loaded && VaultService.Instance.IsUnlocked)
            {
                ApplyFilterAndSearch();
                UpdateUIState();
            }
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
        await WorkspaceDialogs.ShowAsync(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "未导入的文件",
            Content = new ScrollViewer { Content = text, MaxHeight = 340 },
            CloseButtonText = "关闭"
        });
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
        if (_busy || VaultWork.IsBusy) return;

        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.Caption = "松开以加密导入";
            UiMotion.SetVisible(DragOverlay, true, MotionCue.Fade);
        }
    }

    private void VaultPage_DragLeave(object sender, DragEventArgs e)
    {
        UiMotion.SetVisible(DragOverlay, false);
    }

    private async void VaultPage_Drop(object sender, DragEventArgs e)
    {
        UiMotion.SetVisible(DragOverlay, false);
        if (_busy || VaultWork.IsBusy) return;
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
        if (_busy || BatchMode.IsChecked == true) return;
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
        if (_busy || BatchMode.IsChecked == true || e.Key != Windows.System.VirtualKey.Enter || e.KeyStatus.WasKeyDown) return;
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
        if (args.ItemContainer.ContentTemplateRoot is not FrameworkElement root ||
            root.FindName("MediaThumbnail") is not Image image)
        {
            if (!args.InRecycleQueue && args.Phase == 0) args.RegisterUpdateCallback(MediaGridView_ContainerContentChanging);
            return;
        }
        // Recycled templates can remain loaded while the GridView assigns a new item.
        // The container notification pairs the new thumbnail with that exact assignment.
        var item = args.InRecycleQueue ? null : args.Item as VaultItem;
        if (ReferenceEquals(image.Tag, item)) return;
        ReleaseThumbnail(image);
        if (!_loaded || item == null) { _thumbnailImages.Remove(image); return; }
        image.Tag = item;
        _thumbnailImages.Add(image);
        _ = LoadThumbnailAsync(image);
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
        var items = MediaGridView.SelectedItems.OfType<VaultItem>().ToArray();
        if (items.Length != 0) await DoExportAsync(items, BatchMode.IsChecked == true);
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var items = MediaGridView.SelectedItems.OfType<VaultItem>().ToArray();
        if (items.Length != 0) await DoDeleteAsync(items);
    }

    private void BatchMode_Click(object sender, RoutedEventArgs e)
    {
        bool multiple = BatchMode.IsChecked == true;
        var current = MediaGridView.SelectedItem;
        MediaGridView.SelectionMode = multiple ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;
        MediaGridView.SelectedItem = current;
        BatchActions.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionCommands();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => MediaGridView.SelectAll();

    private async void RenameSelected_Click(object sender, RoutedEventArgs e)
    {
        if (MediaGridView.SelectedItem is VaultItem item) await RenameAsync(item);
    }

    private async void FlyoutRename_Click(object sender, RoutedEventArgs e)
    {
        if (FindItemById((sender as FrameworkElement)?.Tag as string) is { } item) await RenameAsync(item);
    }

    private async Task RenameAsync(VaultItem item)
    {
        if (_busy || VaultWork.IsBusy || XamlRoot == null) return;
        string extension = item.FileExtension;
        string current = item.OriginalFileName;
        var input = new TextBox
        {
            Text = extension.Length > 0 && current.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                ? current[..^extension.Length] : current,
            Header = extension.Length == 0 ? "文件名" : $"文件名（保留 {extension}）",
            MaxLength = Math.Max(1, 255 - extension.Length)
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "重命名", Content = input,
            PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
        };
        bool ValidName() => input.Text.Trim().Length > 0 && VaultService.IsValidDisplayName(input.Text.Trim() + extension);
        dialog.IsPrimaryButtonEnabled = ValidName();
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = ValidName();
        input.Loaded += (_, _) => { input.Focus(FocusState.Programmatic); input.SelectAll(); };
        if (await WorkspaceDialogs.ShowAsync(dialog) != ContentDialogResult.Primary || !_loaded) return;
        using var work = VaultWork.TryBegin("正在重命名");
        if (work == null) return;
        try
        {
            await VaultService.Instance.RenameItemAsync(item, input.Text.Trim() + extension, work.Token);
            if (!_loaded) return;
            ApplyFilterAndSearch();
            UpdateUIState();
            if (MediaGridView.ContainerFromItem(item) is GridViewItem container)
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(container, item.OriginalFileName);
            StatusText.Text = $"已重命名为 {item.OriginalFileName}";
        }
        catch (Exception ex) { if (_loaded) StatusText.Text = $"重命名失败：{ex.Message}"; }
    }

    private void FlyoutPlay_Click(object sender, RoutedEventArgs e)
    {
        VaultItem? item = FindItemById((sender as FrameworkElement)?.Tag as string);
        if (item != null) MainWindow.Current?.NavigateToPlayer(item);
    }

    private async void FlyoutExport_Click(object sender, RoutedEventArgs e)
    {
        VaultItem? item = FindItemById((sender as FrameworkElement)?.Tag as string);
        if (item != null) await DoExportAsync([item], false);
    }

    private async void FlyoutDelete_Click(object sender, RoutedEventArgs e)
    {
        VaultItem? item = FindItemById((sender as FrameworkElement)?.Tag as string);
        if (item != null) await DoDeleteAsync([item]);
    }

    private async Task DoExportAsync(IReadOnlyList<VaultItem> items, bool toFolder)
    {
        if (_busy || VaultWork.IsBusy || !VaultService.Instance.IsUnlocked) return;
        SetBusy(true);
        try
        {
            string? destination;
            if (toFolder)
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
                WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.CurrentHandle);
                picker.FileTypeFilter.Add("*");
                destination = (await WorkspaceNativeDialogs.PickAsync(picker.PickSingleFolderAsync))?.Path;
            }
            else
            {
                var item = items[0];
                var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.Downloads, SuggestedFileName = item.OriginalFileName };
                WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.CurrentHandle);
                picker.FileTypeChoices.Add("原媒体格式", new[] { string.IsNullOrEmpty(item.FileExtension) ? ".bin" : item.FileExtension });
                destination = (await WorkspaceNativeDialogs.PickAsync(picker.PickSaveFileAsync))?.Path;
            }
            if (string.IsNullOrEmpty(destination) || !_loaded) return;
            using var work = VaultWork.TryBegin("正在解密导出");
            if (work == null) { StatusText.Text = "请等待当前保险箱任务完成，再重试导出。"; return; }
            ImportProgressBar.Value = 0;
            UiMotion.SetVisible(OperationProgress, true, MotionCue.FromBelow);
            int success = 0;
            string? firstFailure = null;
            for (int i = 0; i < items.Count; i++)
            {
                work.Token.ThrowIfCancellationRequested();
                var item = items[i];
                if (_loaded) StatusText.Text = $"正在解密导出 ({i + 1}/{items.Count}) · {item.OriginalFileName}";
                try
                {
                    if (toFolder && !VaultService.IsValidDisplayName(item.OriginalFileName))
                        throw new IOException("文件名无效，请重命名后导出。");
                    string path = toFolder ? Path.Combine(destination, item.OriginalFileName) : destination;
                    var progress = GetProgress(work, i, items.Count);
                    await VaultService.Instance.ExportItemAsync(item, path, progress, work.Token, useUniqueName: toFolder);
                    success++;
                }
                catch (Exception ex) when (!work.Token.IsCancellationRequested)
                {
                    firstFailure ??= $"{item.OriginalFileName} · {ex.Message}";
                }
            }
            if (_loaded) StatusText.Text = firstFailure == null ? $"已导出 {success} 个解密副本。"
                : $"已导出 {success} 个，失败 {items.Count - success} 个。{firstFailure}";
        }
        catch (Exception ex)
        {
            AppLog.Error("导出媒体失败", ex);
            if (_loaded) StatusText.Text = "导出失败：" + ex.Message;
        }
        finally
        {
            if (_loaded) UiMotion.SetVisible(OperationProgress, false);
            SetBusy(false);
        }
    }

    private IProgress<double> GetProgress(VaultWork work, int completed = 0, int count = 1) =>
        new Progress<double>(value =>
        {
            if (!ReferenceEquals(VaultWork.Current, work) || work.Token.IsCancellationRequested) return;
            double fraction = (completed + value) / count;
            work.Report(fraction);
            if (_loaded && _busy) ImportProgressBar.Value = Math.Max(ImportProgressBar.Value, fraction * 100);
        });

    private async Task DoDeleteAsync(IReadOnlyList<VaultItem> items)
    {
        if (_busy || VaultWork.IsBusy || XamlRoot == null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "彻底粉碎删除？",
            Content = items.Count == 1 ? $"将覆盖并删除「{items[0].OriginalFileName}」。此操作不可恢复。"
                : $"将覆盖并删除选中的 {items.Count} 个加密文件。此操作不可恢复。",
            PrimaryButtonText = "粉碎删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        SetBusy(true);
        try
        {
            var result = await WorkspaceDialogs.ShowAsync(dialog);
            if (result != ContentDialogResult.Primary || !_loaded) return;
            using var work = VaultWork.TryBegin("正在粉碎删除");
            if (work == null) { StatusText.Text = "请等待当前保险箱任务完成，再重试删除。"; return; }
            StatusText.Text = "正在粉碎删除…";
            ImportProgressBar.Value = 0;
            UiMotion.SetVisible(OperationProgress, true, MotionCue.FromBelow);
            var selected = items.ToHashSet();
            var pending = _thumbnailLoads.Where(entry => entry.Key.Tag is VaultItem item && selected.Contains(item)).ToArray();
            foreach (var entry in pending) entry.Value.Token.Cancel();
            await Task.WhenAll(pending.Select(entry => entry.Value.Done.Task));
            await VaultService.Instance.DeleteItemsAsync(items, GetProgress(work), work.Token);
            if (_loaded) StatusText.Text = $"已粉碎删除 {items.Count} 个文件。";
        }
        catch (Exception ex)
        {
            AppLog.Error("删除媒体失败", ex);
            if (_loaded) StatusText.Text = "删除失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false);
            if (_loaded)
            {
                UiMotion.SetVisible(OperationProgress, false);
                ApplyFilterAndSearch();
                UpdateUIState();
            }
        }
    }

    private void SetBusy(bool busy)
    {
        bool finished = _busy && !busy;
        _busy = busy;
        if (_loaded) UpdateSelectionCommands();
        if (finished && _loaded && VaultService.Instance.IsUnlocked)
            UiMotion.Reveal(StatusText, MotionCue.Fade);
    }

    private void MediaGridView_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionCommands();

    private void UpdateSelectionCommands()
    {
        if (!_loaded) return;
        int count = MediaGridView.SelectedItems.Count;
        bool canChange = !_busy && !VaultWork.IsBusy && VaultService.Instance.IsUnlocked;
        ImportButton.IsEnabled = ShredOriginalCheckBox.IsEnabled = canChange;
        PlayButton.IsEnabled = !_busy && count == 1;
        RenameButton.IsEnabled = canChange && count == 1;
        ExportButton.IsEnabled = DeleteButton.IsEnabled = BatchExportButton.IsEnabled = BatchDeleteButton.IsEnabled = canChange && count > 0;
        BatchMode.IsEnabled = !_busy;
        SelectionCount.Text = $"已选 {count} 项";
    }

    #endregion
}
