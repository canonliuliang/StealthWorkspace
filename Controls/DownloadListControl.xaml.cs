using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StealthWorkspace.Models;
using StealthWorkspace.Services;
using Windows.Storage;
using Windows.System;

namespace StealthWorkspace.Controls;

public sealed partial class DownloadListControl : UserControl
{
    private readonly SettingsService.BookmarkSession? _workspace = SettingsService.Instance.ActiveBookmarkSession;
    private bool IsCurrent => BrowserDownloadPath.IsCurrentWorkspace(_workspace);

    public DownloadListControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!IsCurrent) { Detach(); return; }
            if (DownloadList.ItemsSource == null) DownloadList.ItemsSource = BrowserDownloads.Items;
            BrowserDownloads.Items.CollectionChanged -= DownloadsChanged;
            BrowserDownloads.Items.CollectionChanged += DownloadsChanged;
            UpdateEmpty();
        };
        Unloaded += (_, _) => Detach();
    }

    internal void Detach()
    {
        BrowserDownloads.Items.CollectionChanged -= DownloadsChanged;
        DownloadList.ItemsSource = null;
    }

    private void DownloadsChanged(object? sender, NotifyCollectionChangedEventArgs args) => UpdateEmpty();
    private void UpdateEmpty() => UiMotion.SetVisible(EmptyText, BrowserDownloads.Items.Count == 0, MotionCue.Fade);
    private void ClearFinished_Click(object sender, RoutedEventArgs args)
    {
        if (IsCurrent) BrowserDownloads.ClearFinished();
    }
    private void PauseResume_Click(object sender, RoutedEventArgs args) => Apply(sender, item => item.TogglePauseResume());
    private void Cancel_Click(object sender, RoutedEventArgs args) => Apply(sender, item => item.Cancel());
    private async void StoreDownload_Click(object sender, RoutedEventArgs args)
    {
        if (IsCurrent && (sender as FrameworkElement)?.DataContext is BrowserDownloadItem item &&
            BrowserDownloads.Items.Contains(item)) await item.StoreInVaultAsync();
    }

    private void Apply(object sender, Action<BrowserDownloadItem> action)
    {
        if (!IsCurrent || (sender as FrameworkElement)?.DataContext is not BrowserDownloadItem item || !BrowserDownloads.Items.Contains(item)) return;
        try { action(item); ActionError.IsOpen = false; }
        catch (Exception ex) { ShowFailure("暂时无法更改下载状态，请重试。", ex); }
    }

    private async void ShowFile_Click(object sender, RoutedEventArgs args)
    {
        if (!IsCurrent || (sender as FrameworkElement)?.DataContext is not BrowserDownloadItem item || !BrowserDownloads.Items.Contains(item)) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
            var folder = await file.GetParentAsync();
            if (!IsCurrent || !BrowserDownloads.Items.Contains(item)) return;
            var options = new FolderLauncherOptions();
            options.ItemsToSelect.Add(file);
            if (!await Launcher.LaunchFolderAsync(folder, options)) throw new IOException("Folder launch declined");
        }
        catch (Exception ex) { ShowFailure("无法定位文件，可能已被移动或清理。", ex); }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs args)
    {
        if (!IsCurrent) return;
        try
        {
            string directory = AppPaths.IsolatedDownloadsRoot;
            Directory.CreateDirectory(directory);
            AppPaths.Hide(directory);
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            if (!IsCurrent) return;
            if (!await Launcher.LaunchFolderAsync(folder)) throw new IOException("Folder launch declined");
        }
        catch (Exception ex) { ShowFailure("无法打开下载文件夹，请稍后重试。", ex); }
    }

    private void ShowFailure(string message, Exception ex)
    {
        ActionError.Message = message;
        UiMotion.Open(ActionError);
        AppLog.Warn("Download list action: " + ex.GetType().Name);
    }
}
