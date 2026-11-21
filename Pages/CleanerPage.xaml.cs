using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StealthWorkspace.Services;

namespace StealthWorkspace.Pages;

public sealed partial class CleanerPage : Page
{
    private bool _isBusy;
    private bool _isPageLoaded;
    private CancellationTokenSource? _operation;

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
            AccessPanel.Visibility = CleanerAccess.IsRestricted ? Visibility.Visible : Visibility.Collapsed;

            if (Items.Count > 0) return;

            foreach (CleanerItem category in CleanerService.Instance.GetDefaultCategories())
            {
                if (category.Category == CleanerCategory.ThumbnailCache)
                    RestartExplorerCheck.IsEnabled = category.CanClean;
                category.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(CleanerItem.IsSelected)) UpdateCleanAction();
                };
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
        CancelOperation();
    }

    internal void CancelOperation() => _operation?.Cancel();

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
        ApplySelection(item => item.Category == CleanerCategory.TempFiles);
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
        foreach (CleanerItem item in Items)
        {
            item.IsSelected = item.CanClean && predicate(item);
        }
    }

    private async void DetailsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_isPageLoaded || this.XamlRoot is null) return;
        if ((sender as FrameworkElement)?.Tag is not CleanerItem item || item.PreviewEntries.Count == 0) return;

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{item.Name} — 共检测到 {item.DetectedCount} 项（以下展示前 {item.PreviewEntries.Count} 条样本）：",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap
        });

        var listBox = new ListView
        {
            ItemsSource = item.PreviewEntries,
            MaxHeight = 320,
            SelectionMode = ListViewSelectionMode.None,
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
            await WorkspaceDialogs.ShowAsync(dialog);
        }
        catch (Exception ex)
        {
            AppLog.Warn("显示清理明细对话框失败", ex);
        }
    }

    private async void CleanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || !_isPageLoaded || !VaultService.Instance.IsUnlocked) return;

        List<CleanerItem> snapshot = Items.ToList();
        List<CleanerItem> selected = snapshot.Where(item => item.CanClean && item.IsSelected).ToList();
        CleanerOptions options = BuildOptions();

        if (selected.Count == 0)
        {
            ShowOperationWarning("未选择清理项", "请先勾选至少一个需要清理的类别。");
            return;
        }

        using var operation = new CancellationTokenSource();
        _operation = operation;
        SetBusy(true, showProgress: false);
        MainWindow.Current?.SetPageNavigationBlocked(this, true);
        HideInfoBar();

        try
        {
            // Recheck the selection before showing irreversible actions. A file held during
            // the previous run may have become available since the list was last refreshed.
            selected = await CleanerService.Instance.ScanAsync(selected, options: options, cancellationToken: operation.Token);
            if (!_isPageLoaded || !VaultService.Instance.IsUnlocked) return;
            bool confirmed = await ShowCleanConfirmationAsync(selected, options);
            if (!confirmed || operation.IsCancellationRequested)
            {
                if (_isPageLoaded)
                {
                    SummaryText.Text = "已取消清理，未对系统做任何改动。";
                }
                return;
            }

            if (!_isPageLoaded || !VaultService.Instance.IsUnlocked) return;

            // Cleaning the isolated folder can remove the source of an in-flight import.
            bool touchesDownloads = selected.Any(item => item.Category == CleanerCategory.IsolatedDownloads);
            using var downloadWork = touchesDownloads ? VaultWork.TryBegin("清理下载文件") : null;
            if (touchesDownloads && downloadWork == null)
            {
                ShowOperationWarning("保险箱任务进行中", "请在当前任务结束后再清理下载文件。");
                return;
            }
            OpProgressRing.IsActive = true;
            var progress = CreateProgress(operation.Token);
            SummaryText.Text = "正在执行清理…";

            List<CleanerItem> cleanResults = await CleanerService.Instance.CleanAsync(selected, options, progress, operation.Token);
            string cleanSummary = BuildCleanSummary(cleanResults);
            Dictionary<CleanerCategory, CleanerItem> cleanedByCategory = cleanResults
                .Where(item => item.IsSelected)
                .ToDictionary(item => item.Category);

            if (!_isPageLoaded) return;

            foreach (CleanerItem existing in Items)
            {
                if (cleanedByCategory.TryGetValue(existing.Category, out CleanerItem? cleaned))
                {
                    existing.ApplyScanResult(cleaned);
                }
            }

            bool hasFailure = cleanResults.Any(r => r.IsSelected && r.Outcome == CleanOutcome.Failed);
            bool hasSkipped = cleanResults.Any(r => r.IsSelected && r.Outcome == CleanOutcome.Skipped);
            SummaryText.Text = "正在刷新剩余项目…";
            try
            {
                // Merge selection state from snapshot into refreshed list, while preserving detailed clean status text.
                List<CleanerItem> refreshed = await CleanerService.Instance.ScanAsync(snapshot, progress, options, operation.Token);
                if (!_isPageLoaded) return;

                foreach (CleanerItem item in refreshed)
                {
                    if (cleanedByCategory.TryGetValue(item.Category, out CleanerItem? cleaned) &&
                        !string.IsNullOrWhiteSpace(cleaned.Status))
                    {
                        item.Status = cleaned.Status;
                        item.Outcome = cleaned.Outcome;
                    }
                }

                ApplyResults(refreshed);
                UpdateStatsBadge(refreshed);
                SummaryText.Text = "可以在类别列表中查看各项处理结果。";
                if (hasFailure)
                {
                    ShowInfoBar(InfoBarSeverity.Warning, "部分清理未完成", cleanSummary);
                }
                else if (hasSkipped)
                {
                    ShowInfoBar(InfoBarSeverity.Informational, "部分项目已保留", cleanSummary);
                }
                else
                {
                    bool noWork = cleanResults.Where(item => item.IsSelected).All(item => item.Outcome == CleanOutcome.NothingToClean);
                    ShowInfoBar(noWork ? InfoBarSeverity.Informational : InfoBarSeverity.Success,
                        noWork ? "所选类别没有待清理的项目" : "清理已完成", noWork ? string.Empty : cleanSummary);
                }
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
                if (_isPageLoaded) SummaryText.Text = cleanSummary + "（重新扫描已停止）";
            }
            catch (Exception ex)
            {
                AppLog.Error("清理后重新扫描失败", ex);
                SummaryText.Text = cleanSummary + "（列表刷新失败，可手动点击“重新扫描”重试）";
                ShowInfoBar(InfoBarSeverity.Warning, "清理已完成，但列表刷新失败", "可手动点击“重新扫描”重试。");
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (_isPageLoaded) SummaryText.Text = "清理已停止。已完成的清理仍然生效，可重新扫描查看剩余项目。";
        }
        catch (Exception ex)
        {
            AppLog.Error("执行清理失败", ex);
            ShowOperationError("清理失败", "清理过程中发生错误，部分项目可能未完成（详情见诊断日志）。");
        }
        finally
        {
            _operation = null;
            MainWindow.Current?.SetPageNavigationBlocked(this, false);
            SetBusy(false);
        }
    }

    private async Task ScanAllAsync()
    {
        if (_isBusy || !_isPageLoaded) return;

        using var operation = new CancellationTokenSource();
        _operation = operation;
        SetBusy(true);
        HideInfoBar();
        SummaryText.Text = "正在检查可访问的文件和记录…";

        try
        {
            CleanerOptions options = BuildOptions();
            var progress = CreateProgress(operation.Token);
            List<CleanerItem> results = await CleanerService.Instance.ScanAsync(Items.ToList(), progress, options, operation.Token);
            if (!_isPageLoaded) return;

            ApplyResults(results);
            UpdateStatsBadge(results);
            SummaryText.Text = BuildScanSummary(results);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (_isPageLoaded) SummaryText.Text = "扫描已停止，可重新扫描。";
        }
        catch (Exception ex)
        {
            AppLog.Error("扫描系统痕迹失败", ex);
            ShowOperationError("扫描失败", "扫描过程中发生错误，请稍后重试（详情见诊断日志）。");
        }
        finally
        {
            _operation = null;
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
            ContentDialogResult result = await WorkspaceDialogs.ShowAsync(dialog);
            return result == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            AppLog.Error("显示清理确认对话框失败", ex);
            ShowOperationError("无法确认清理", "确认对话框未能显示，已取消本次清理。");
            return false;
        }
    }

    private IProgress<string> CreateProgress(CancellationToken token) => new Progress<string>(message =>
    {
        if (_operation?.Token != token || token.IsCancellationRequested || !_isPageLoaded || !OpProgressRing.IsActive) return;
        SummaryText.Text = message;
    });

    private void SetBusy(bool busy, bool showProgress = true)
    {
        _isBusy = busy;
        ScanBtn.IsEnabled = !busy;
        UpdateCleanAction();
        PresetPrivacyBtn.IsEnabled = !busy;
        SelectAllBtn.IsEnabled = !busy;
        DeselectAllBtn.IsEnabled = !busy;
        RestartExplorerCheck.IsEnabled = !busy && !CleanerAccess.IsRestricted && !AppPaths.HasDataRootOverride;
        DeepTempCheck.IsEnabled = !busy;
        SecureShredCheck.IsEnabled = !busy;
        CleanerListView.IsEnabled = !busy;
        OpProgressRing.IsActive = busy && showProgress;
    }

    private void UpdateCleanAction() => CleanBtn.IsEnabled = !_isBusy && Items.Any(item => item.CanClean && item.IsSelected);

    private async void OpenStorage_Click(object sender, RoutedEventArgs e)
    {
        bool opened = false;
        try { opened = await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:storagesense")); }
        catch (Exception ex) { AppLog.Warn("打开 Windows 存储设置失败", ex); }
        if (!opened && _isPageLoaded)
            ShowOperationWarning("Windows 存储设置未打开", "请在 Windows 设置中打开「系统 → 存储 → 临时文件」。");
    }

    private void ApplyResults(IReadOnlyList<CleanerItem> results)
    {
        // ScanAsync produces one result for each input, in the original order.
        for (int i = 0; i < results.Count; i++)
        {
            Items[i].ApplyScanResult(results[i]);
        }
    }

    private void UpdateStatsBadge(IReadOnlyList<CleanerItem> results)
    {
        int totalCount = results.Sum(r => r.DetectedCount);
        long totalBytes = results.Where(r => r.IsSizeKnown).Sum(r => r.DetectedSizeBytes);

        string label = totalBytes > 0 ? $"已统计 {totalCount} 项 · {CleanerService.FormatSize(totalBytes)}"
            : totalCount > 0 ? $"已统计 {totalCount} 项" : "已统计 0 项";
        if (StatsBadgeText.Text == label) return;
        StatsBadgeText.Text = label;
        UiMotion.Reveal(StatsBadgeText, MotionCue.Fade);
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
        int count = results.Where(item => item.CanClean).Sum(item => item.DetectedCount);
        bool partial = results.Any(item => item.CanClean && item.Outcome is CleanOutcome.Failed or CleanOutcome.Skipped);
        if (partial) return $"已统计 {count} 项。有些内容受限或正被占用，各类别中列出了原因。";
        return count == 0 ? "可扫描的类别中未发现待处理文件或记录。" :
            $"已统计 {count} 项。可以在各类别中查看明细，再清理选中项。";
    }

    internal static string BuildCleanSummary(IReadOnlyList<CleanerItem> results)
    {
        var selected = results.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0) return "未选择类别。";
        var parts = new List<string>();
        foreach (var (state, label) in new[] {
            (CleanOutcome.Complete, "完成"), (CleanOutcome.NothingToClean, "无需处理"),
            (CleanOutcome.Skipped, "有保留"), (CleanOutcome.Failed, "未完成") })
        {
            int count = selected.Count(item => item.Outcome == state);
            if (count > 0) parts.Add($"{label} {count} 类");
        }
        return string.Join(" · ", parts);
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
