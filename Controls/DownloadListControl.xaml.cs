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
    public DownloadListControl()
    {
        InitializeComponent();
        DownloadList.ItemsSource = BrowserDownloads.Items;
        Loaded += (_, _) => { BrowserDownloads.Items.CollectionChanged += DownloadsChanged; UpdateEmpty(); };
        Unloaded += (_, _) => BrowserDownloads.Items.CollectionChanged -= DownloadsChanged;
    }

    private void DownloadsChanged(object? sender, NotifyCollectionChangedEventArgs args) => UpdateEmpty();
    private void UpdateEmpty() => UiMotion.SetVisible(EmptyText, BrowserDownloads.Items.Count == 0, MotionCue.Fade);
    private void ClearFinished_Click(object sender, RoutedEventArgs args) => BrowserDownloads.ClearFinished();
    private void PauseResume_Click(object sender, RoutedEventArgs args) => Apply(sender, item => item.TogglePauseResume());
    private void Cancel_Click(object sender, RoutedEventArgs args) => Apply(sender, item => item.Cancel());
    private async void StoreDownload_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserDownloadItem item) await item.StoreInVaultAsync();
    }

    private void Apply(object sender, Action<BrowserDownloadItem> action)
    {
        if ((sender as FrameworkElement)?.DataContext is not BrowserDownloadItem item) return;
        try { action(item); ActionError.IsOpen = false; }
        catch (Exception ex) { ShowFailure("暂时无法更改下载状态，请重试。", ex); }
    }

    private async void ShowFile_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not BrowserDownloadItem item) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
            var folder = await file.GetParentAsync();
            var options = new FolderLauncherOptions();
            options.ItemsToSelect.Add(file);
            if (!await Launcher.LaunchFolderAsync(folder, options)) throw new IOException("Folder launch declined");
        }
        catch (Exception ex) { ShowFailure("无法定位文件，可能已被移动或清理。", ex); }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.IsolatedDownloadsRoot);
            AppPaths.Hide(AppPaths.IsolatedDownloadsRoot);
            var folder = await StorageFolder.GetFolderFromPathAsync(AppPaths.IsolatedDownloadsRoot);
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
