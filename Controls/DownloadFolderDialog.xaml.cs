using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed record DownloadFolderLocation(string Name, string Directory);
public sealed record DownloadFolderFile(string Path, string Name, DateTime Modified, string Detail);

public sealed partial class DownloadFolderDialog : ContentDialog
{
    private readonly SettingsService.BookmarkSession? _workspace;
    private readonly CancellationTokenSource _lifetime = new();
    private int _scanVersion;
    private bool IsCurrent => !_lifetime.IsCancellationRequested && BrowserDownloadPath.IsCurrentWorkspace(_workspace);

    internal DownloadFolderDialog(XamlRoot root, SettingsService.BookmarkSession? workspace)
    {
        InitializeComponent();
        XamlRoot = root;
        _workspace = workspace;
        Opened += (_, _) =>
        {
            if (SmokeTest.BrowserAudit) SmokeTest.Report("downloads folder dialog opened");
            if (!IsCurrent) { Hide(); return; }
            try
            {
                var current = BrowserDownloadFolders.Prepare(_workspace);
                var places = new List<DownloadFolderLocation> { new(_workspace!.Temporary ? "本次临时下载" : "当前下载位置", current) };
                if (BrowserDownloadFolders.Previous(_workspace) is { } previous) places.Add(new("另一下载位置", previous));
                Locations.ItemsSource = places;
                Locations.SelectedIndex = 0;
            }
            catch (Exception ex) { FolderStatus.Text = BrowserDownloadFolders.FailureMessage(ex); }
        };
        Closed += (_, _) => { _lifetime.Cancel(); Files.ItemsSource = null; Locations.ItemsSource = null; };
    }

    internal static Task ShowFolderAsync(XamlRoot root, SettingsService.BookmarkSession? workspace) =>
        WorkspaceDialogs.ShowAsync(new DownloadFolderDialog(root, workspace));

    private async void LocationChanged(object sender, SelectionChangedEventArgs e) => await RefreshAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (!IsCurrent || Locations.SelectedItem is not DownloadFolderLocation location) return;
        FolderAddress.Text = location.Directory;
        int version = ++_scanVersion;
        Files.ItemsSource = null;
        FolderStatus.Text = "正在读取文件…";
        try
        {
            var result = await Task.Run(() =>
            {
                var entries = new List<DownloadFolderFile>();
                int skipped = 0;
                if (!SafeFileSystem.IsPlainPath(location.Directory)) throw new UnauthorizedAccessException();
                foreach (var info in new DirectoryInfo(location.Directory).EnumerateFiles())
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    try
                    {
                        if (info.Name.StartsWith(".write-", StringComparison.Ordinal) || info.Extension.Equals(".crdownload", StringComparison.OrdinalIgnoreCase)
                            || SafeFileSystem.HasLinkReparsePoint(info.FullName)) continue;
                        double kib = info.Length / 1024d;
                        string size = kib >= 1024 ? $"{kib / 1024:N1} MB" : $"{Math.Ceiling(kib):N0} KB";
                        entries.Add(new(info.FullName, info.Name, info.LastWriteTime, size + " · " + info.LastWriteTime.ToString("g", CultureInfo.CurrentCulture)));
                    }
                    catch (FileNotFoundException) { } // Replaced while enumerating.
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                }
                return (Files: entries.OrderByDescending(f => f.Modified).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), Skipped: skipped);
            }, _lifetime.Token);
            if (!IsCurrent || _scanVersion != version) return;
            Files.ItemsSource = result.Files;
            FolderStatus.Text = result.Skipped > 0 ? $"{result.Files.Length} 个文件，另有 {result.Skipped} 项暂时无法读取。"
                : result.Files.Length == 0 ? "此位置还没有已下载文件。" : $"{result.Files.Length} 个文件";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (IsCurrent && version == _scanVersion) FolderStatus.Text = BrowserDownloadFolders.FailureMessage(ex); }
    }

    private void FileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OpenExplorer == null || StoreFile == null) return;
        bool selected = Files.SelectedItem is DownloadFolderFile;
        OpenExplorer.Content = selected ? "在资源管理器中显示所选文件" : "在资源管理器中打开";
        StoreFile.IsEnabled = IsCurrent && selected;
    }

    private async void OpenExplorer_Click(object sender, RoutedEventArgs args)
    {
        if (!IsCurrent || Locations.SelectedItem is not DownloadFolderLocation location) return;
        try
        {
            if (Files.SelectedItem is DownloadFolderFile item) BrowserDownloadFolders.SelectFile(item.Path);
            else await BrowserDownloadFolders.OpenAsync(location.Directory, () => IsCurrent && Equals(Locations.SelectedItem, location));
        }
        catch (Exception ex)
        {
            FolderStatus.Text = BrowserDownloadFolders.FailureMessage(ex);
            AppLog.Warn($"Download folder shell: {ex.GetType().Name} (0x{ex.HResult:X8})");
            if (SmokeTest.BrowserAudit) SmokeTest.Report($"folder shell error: {ex.GetType().Name} (0x{ex.HResult:X8}) native={(ex as System.ComponentModel.Win32Exception)?.NativeErrorCode}");
        }
    }

    private async void Store_Click(object sender, RoutedEventArgs args)
    {
        if (!IsCurrent || Files.SelectedItem is not DownloadFolderFile item) return;
        using var work = VaultWork.TryBegin("下载入库");
        if (work == null) { FolderStatus.Text = "媒体库中有任务正在进行，请稍后再试。"; return; }
        try
        {
            if (!SafeFileSystem.IsPlainPath(item.Path) || SafeShred.IsHardLink(item.Path)) throw new IOException("文件位置已改变。");
            StoreFile.IsEnabled = false;
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(work.Token, _lifetime.Token);
            using var handle = File.OpenHandle(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var identity = SafeShred.CaptureIdentity(handle);
            await VaultService.Instance.ImportDownloadAsync(item.Path, identity, _workspace, false, new Progress<double>(work.Report), cancel.Token);
            if (IsCurrent) FolderStatus.Text = "已复制到加密媒体库，下载原件已保留。";
        }
        catch (OperationCanceledException) { if (IsCurrent) FolderStatus.Text = "复制已取消。"; }
        catch (UnauthorizedAccessException) { if (IsCurrent) FolderStatus.Text = "Windows 拒绝了文件读取或媒体库写入。下载原件已保留。"; }
        catch (IOException) { if (IsCurrent) FolderStatus.Text = "文件可能已移动、被占用或磁盘空间不足。下载原件已保留。"; }
        catch (Exception ex) { if (IsCurrent) { FolderStatus.Text = "未能复制到加密媒体库，下载原件已保留。"; AppLog.Warn("Download copy: " + ex.GetType().Name); } }
        finally { if (IsCurrent) StoreFile.IsEnabled = Files.SelectedItem != null; }
    }
}
