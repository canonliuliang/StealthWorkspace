using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StealthWorkspace.Services;

namespace StealthWorkspace.Pages;

public sealed partial class CleanerPage : Page
{
    private bool _isBusy;
    private bool _isPageLoaded;

    public ObservableCollection<CleanerItem> Items { get; } = new();

    public CleanerPage()
    {
        InitializeComponent();
        Loaded += CleanerPage_Loaded;
        Unloaded += CleanerPage_Unloaded;
    }

    private async void CleanerPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _isPageLoaded = true;
            CleanerListView.ItemsSource = Items;

            if (Items.Count > 0) return;

            foreach (CleanerItem category in CleanerService.Instance.GetDefaultCategories())
            {
                Items.Add(category);
            }

            await ScanAllAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("初始化清理页面失败", ex);
            ShowOperationError("初始化失败", "清理页面初始化过程中发生错误，请重新打开页面。");
        }
    }

    private void CleanerPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _isPageLoaded = false;
    }

    private CleanerOptions BuildOptions()
    {
        return new CleanerOptions
        {
            RestartExplorerForThumbnails = RestartExplorerCheck.IsChecked ?? false,
            DeepCleanTemp = DeepTempCheck.IsChecked ?? false,
            SecureShredFiles = SecureShredCheck.IsChecked ?? false
        };
    }

    private async void ScanBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ScanAllAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("扫描按钮处理失败", ex);
            ShowOperationError("扫描失败", "扫描过程中发生错误，请稍后重试（详情见诊断日志）。");
        }
    }

    private async void DeepTempCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isPageLoaded || _isBusy || Items.Count == 0) return;
        await ScanAllAsync();
    }

    private void PresetPrivacyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        ApplySelection(item => item.Category != CleanerCategory.IsolatedDownloads);
    }

    private void SelectAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        ApplySelection(_ => true);
    }

    private void DeselectAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        ApplySelection(_ => false);
    }

    private void ApplySelection(Func<CleanerItem, bool> predicate)
    {
        var updated = Items.Select(item => new CleanerItem
        {
            Category = item.Category,
            GroupName = item.GroupName,
            IconGlyph = item.IconGlyph,
            Name = item.Name,
            Description = item.Description,
            IsSelected = predicate(item),
            Status = item.Status,
            DetectedCount = item.DetectedCount,
            DetectedSizeBytes = item.DetectedSizeBytes,
            IsSizeKnown = item.IsSizeKnown,
            PreviewEntries = item.PreviewEntries
        }).ToList();

        ReplaceItems(updated);
    }

    private async void DetailsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_isPageLoaded || this.XamlRoot is null) return;
        if ((sender as FrameworkElement)?.Tag is not CleanerItem item || item.PreviewEntries.Count == 0) return;

        var panel = new StackPanel { Spacing = 10, MinWidth = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{item.Name} — 共检测到 {item.DetectedCount} 项（以下展示前 {item.PreviewEntries.Count} 条样本）：",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });

        var listBox = new ListView
        {
            ItemsSource = item.PreviewEntries,
            MaxHeight = 320,
            SelectionMode = ListViewSelectionMode.None,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };

        panel.Children.Add(listBox);

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "扫描明细预览",
            Content = panel,
            CloseButtonText = "关闭"
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("显示清理明细对话框失败", ex);
        }
    }

    private async void CleanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || !_isPageLoaded) return;

        List<CleanerItem> snapshot = Items.ToList();
        List<CleanerItem> selected = snapshot.Where(item => item.IsSelected).ToList();
        CleanerOptions options = BuildOptions();

        if (selected.Count == 0)
        {
            ShowOperationWarning("未选择清理项", "请先勾选至少一个需要清理的类别。");
            return;
        }

        SetBusy(true);
        HideInfoBar();

        try
        {
            bool confirmed = await ShowCleanConfirmationAsync(selected, options);
            if (!confirmed)
            {
                if (_isPageLoaded)
                {
                    SummaryText.Text = "已取消清理，未对系统做任何改动。";
                }
                return;
            }

            if (!_isPageLoaded) return;

            var progress = new Progress<string>(ShowProgressMessage);
            SummaryText.Text = "正在执行清理…";

            List<CleanerItem> cleanResults = await CleanerService.Instance.CleanAsync(selected, options, progress);
            string cleanSummary = BuildCleanSummary(cleanResults);

            if (!_isPageLoaded) return;

            SummaryText.Text = cleanSummary + " 正在重新扫描…";
            try
            {
                // Merge selection state from snapshot into refreshed list
                List<CleanerItem> refreshed = await CleanerService.Instance.ScanAsync(snapshot, progress, options);
                if (!_isPageLoaded) return;

                ReplaceItems(refreshed);
                UpdateStatsBadge(refreshed);
                SummaryText.Text = cleanSummary + "（列表已重新扫描）";
            }
            catch (Exception ex)
            {
                AppLog.Error("清理后重新扫描失败", ex);
                SummaryText.Text = cleanSummary + "（列表刷新失败，可手动点击“重新扫描”重试）";
                ShowInfoBar(InfoBarSeverity.Warning, "清理已完成，但列表刷新失败", "可手动点击“重新扫描”重试。");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("执行清理失败", ex);
            ShowOperationError("清理失败", "清理过程中发生错误，部分项目可能未完成（详情见诊断日志）。");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ScanAllAsync()
    {
        if (_isBusy || !_isPageLoaded) return;

        SetBusy(true);
        HideInfoBar();
        SummaryText.Text = "正在全面扫描系统隐私痕迹…";

        try
        {
            CleanerOptions options = BuildOptions();
            var progress = new Progress<string>(ShowProgressMessage);
            List<CleanerItem> results = await CleanerService.Instance.ScanAsync(Items.ToList(), progress, options);
            if (!_isPageLoaded) return;

            ReplaceItems(results);
            UpdateStatsBadge(results);
            SummaryText.Text = BuildScanSummary(results);
        }
        catch (Exception ex)
        {
            AppLog.Error("扫描系统痕迹失败", ex);
            ShowOperationError("扫描失败", "扫描过程中发生错误，请稍后重试（详情见诊断日志）。");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<bool> ShowCleanConfirmationAsync(IReadOnlyList<CleanerItem> selected, CleanerOptions options)
    {
        if (!_isPageLoaded || this.XamlRoot is null) return false;

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = $"即将清理以下 {selected.Count} 个类别，操作不可撤销：",
            TextWrapping = TextWrapping.Wrap
        });

        foreach (CleanerItem item in selected)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"• {item.Name}{DescribePending(item)}",
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (selected.Any(i => i.Category == CleanerCategory.ThumbnailCache))
        {
            content.Children.Add(new TextBlock
            {
                Text = options.RestartExplorerForThumbnails
                    ? "提示：为解锁缩略图文件，将短暂结束资源管理器进程，清理完成后自动重启。"
                    : "提示：未勾选“允许短暂重启资源管理器”，正被占用的缩略图文件会被跳过。",
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (selected.Any(i => i.Category == CleanerCategory.RecycleBin))
        {
            content.Children.Add(new TextBlock
            {
                Text = "注意：Windows 回收站中的文件将被永久清空，无法从回收站还原。",
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (selected.Any(i => i.Category == CleanerCategory.TempFiles))
        {
            content.Children.Add(new TextBlock
            {
                Text = options.DeepCleanTemp
                    ? "提示：已开启深度 %TEMP% 模式，将清理包括 24 小时内生成的文件与非空子目录（自动避开当前运行中的浏览器沙箱）。"
                    : "提示：仅清理 %TEMP% 根目录中最后修改时间早于 24 小时的文件。",
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (selected.Any(i => i.Category == CleanerCategory.IsolatedDownloads))
        {
            content.Children.Add(new TextBlock
            {
                Text = "注意：已勾选“工作台沙箱隔离下载文件”，沙箱内保存的下载文件将被永久删除。",
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (options.SecureShredFiles)
        {
            content.Children.Add(new TextBlock
            {
                Text = "提示：已启用安全覆写粉碎，删除文件前将执行多轮覆写，耗时可能增加。",
                TextWrapping = TextWrapping.Wrap
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "确认清理系统痕迹？",
            Content = content,
            PrimaryButtonText = "确认清理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        try
        {
            ContentDialogResult result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            AppLog.Error("显示清理确认对话框失败", ex);
            ShowOperationError("无法确认清理", "确认对话框未能显示，已取消本次清理。");
            return false;
        }
    }

    private void ShowProgressMessage(string message)
    {
        if (!_isPageLoaded) return;
        SummaryText.Text = message;
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        ScanBtn.IsEnabled = !busy;
        CleanBtn.IsEnabled = !busy;
        PresetPrivacyBtn.IsEnabled = !busy;
        SelectAllBtn.IsEnabled = !busy;
        DeselectAllBtn.IsEnabled = !busy;
        RestartExplorerCheck.IsEnabled = !busy;
        DeepTempCheck.IsEnabled = !busy;
        SecureShredCheck.IsEnabled = !busy;
        CleanerListView.IsEnabled = !busy;
        OpProgressRing.IsActive = busy;
    }

    private void ReplaceItems(IReadOnlyList<CleanerItem> items)
    {
        CleanerListView.ItemsSource = null;
        Items.Clear();
        foreach (CleanerItem item in items)
        {
            Items.Add(item);
        }
        CleanerListView.ItemsSource = Items;
    }

    private void UpdateStatsBadge(IReadOnlyList<CleanerItem> results)
    {
        int totalCount = results.Sum(r => r.DetectedCount);
        long totalBytes = results.Where(r => r.IsSizeKnown).Sum(r => r.DetectedSizeBytes);

        if (totalCount == 0 && totalBytes == 0)
        {
            StatsBadgeText.Text = "系统干净";
        }
        else if (totalBytes > 0)
        {
            StatsBadgeText.Text = $"发现 {totalCount} 项 · {CleanerService.FormatSize(totalBytes)}";
        }
        else
        {
            StatsBadgeText.Text = $"发现 {totalCount} 条痕迹记录";
        }
    }

    private void HideInfoBar() => StatusInfoBar.IsOpen = false;

    private void ShowInfoBar(InfoBarSeverity severity, string title, string message)
    {
        if (!_isPageLoaded) return;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        UiMotion.Open(StatusInfoBar);
    }

    private void ShowOperationWarning(string title, string message)
    {
        if (!_isPageLoaded) return;
        SummaryText.Text = message;
        ShowInfoBar(InfoBarSeverity.Warning, title, message);
    }

    private void ShowOperationError(string title, string message)
    {
        if (!_isPageLoaded) return;
        SummaryText.Text = message;
        ShowInfoBar(InfoBarSeverity.Error, title, message);
    }

    private static string BuildScanSummary(IReadOnlyList<CleanerItem> results)
    {
        int categoriesWithFindings = results.Count(item => item.DetectedCount > 0 || item.DetectedSizeBytes > 0);
        if (categoriesWithFindings == 0)
        {
            return "扫描完成：暂未发现文件或注册表痕迹；剪贴板与 DNS 缓存仍可随时手动清空。";
        }

        int totalItems = results.Sum(item => item.DetectedCount);
        long totalSize = results.Where(item => item.IsSizeKnown).Sum(item => item.DetectedSizeBytes);
        string sizePart = totalSize > 0 ? $"，可释放约 {CleanerService.FormatSize(totalSize)}" : string.Empty;
        return $"扫描完成：{categoriesWithFindings} 个类别发现可清理内容（共 {totalItems} 项{sizePart}）。可点击右侧“明细”预览，确认后点击“开始清理”。";
    }

    private static string BuildCleanSummary(IReadOnlyList<CleanerItem> results)
    {
        var parts = new List<string>();
        foreach (CleanerItem item in results)
        {
            if (!item.IsSelected) continue;
            parts.Add(string.IsNullOrWhiteSpace(item.Status) ? item.Name : item.Status);
        }

        return parts.Count == 0
            ? "本次清理未执行任何删除。"
            : "本次清理结果：" + string.Join("；", parts) + "。";
    }

    private static string DescribePending(CleanerItem item)
    {
        return item.Category switch
        {
            CleanerCategory.Clipboard => "（将立即清空）",
            CleanerCategory.DnsCache => "（将调用系统接口刷新）",
            _ => item.DetectedCount > 0
                ? (item.IsSizeKnown
                    ? $"（{item.DetectedCount} 项，{CleanerService.FormatSize(item.DetectedSizeBytes)}）"
                    : $"（{item.DetectedCount} 条记录）")
                : "（当前未发现待清理项）"
        };
    }
}
