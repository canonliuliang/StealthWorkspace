using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace StealthWorkspace.Models;

public sealed class BrowserDownloadItem : INotifyPropertyChanged
{
    public override string ToString() => FileName + " · " + Status;
    private CoreWebView2DownloadOperation? _operation;
    private Action? _wakeOwner;
    private long _lastProgressNotification;
    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public string Status { get; private set; } = "正在开始下载";
    public double Percent { get; private set; }
    public bool IsIndeterminate { get; private set; }
    public bool IsRunning { get; private set; }
    public bool CanResume { get; private set; }
    public bool IsCompleted { get; private set; }
    public bool IsPending => IsRunning || CanResume;
    public string ActionLabel => IsRunning ? "暂停" : "继续";
    public string ActionName => ActionLabel + "下载 " + FileName;
    public Visibility ActionVisibility => IsPending ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CompletedVisibility => IsCompleted ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal BrowserDownloadItem(CoreWebView2DownloadOperation operation, string path, Action wakeOwner)
    {
        _operation = operation;
        _wakeOwner = wakeOwner;
        FilePath = path;
        Update(operation, true);
    }

    internal void Update(CoreWebView2DownloadOperation operation, bool stateChanged)
    {
        if (!stateChanged && Environment.TickCount64 - _lastProgressNotification < 250) return;
        _lastProgressNotification = Environment.TickCount64;
        IsRunning = operation.State == CoreWebView2DownloadState.InProgress;
        CanResume = operation.State == CoreWebView2DownloadState.Interrupted && operation.CanResume;
        IsCompleted = operation.State == CoreWebView2DownloadState.Completed;
        long received = operation.BytesReceived;
        long? total = operation.TotalBytesToReceive;
        IsIndeterminate = IsRunning && (!total.HasValue || total <= 0);
        Percent = IsCompleted ? 100 : total > 0 ? Math.Clamp(received * 100.0 / total.Value, 0, 100) : 0;
        string size = total > 0 ? $"{FormatBytes(received)} / {FormatBytes(total.Value)}" : FormatBytes(received);
        Status = IsCompleted ? $"已完成 · {FormatBytes(received)}" : IsRunning ? size : CanResume
            ? (IsUserPause(operation.InterruptReason) ? "已暂停 · " : "下载中断，可继续 · ") + size
            : operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled ? "已取消" : "下载未完成 · " + DescribeFailure(operation.InterruptReason);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        if (!IsPending) { _operation = null; _wakeOwner = null; }
    }

    internal static bool IsUserPause(CoreWebView2DownloadInterruptReason reason) => reason is
        CoreWebView2DownloadInterruptReason.UserPaused or CoreWebView2DownloadInterruptReason.UserCanceled;

    public void TogglePauseResume()
    {
        if (_operation is not { } operation) return;
        if (IsRunning) operation.Pause();
        else if (CanResume) { _wakeOwner?.Invoke(); operation.Resume(); }
    }

    public void Cancel()
    {
        if (_operation is not { } operation) return;
        _wakeOwner?.Invoke();
        operation.Cancel();
    }

    internal void Detach(string message = "下载已停止")
    {
        _operation = null;
        _wakeOwner = null;
        if (!IsPending) return;
        IsRunning = CanResume = false;
        IsIndeterminate = false;
        Status = message;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static string FormatBytes(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824.0:0.0} GB"
        : bytes >= 1048576 ? $"{bytes / 1048576.0:0.0} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0} KB" : $"{bytes} B";

    private static string DescribeFailure(CoreWebView2DownloadInterruptReason reason) => reason switch
    {
        CoreWebView2DownloadInterruptReason.FileNoSpace => "磁盘空间不足",
        CoreWebView2DownloadInterruptReason.FileAccessDenied => "没有文件写入权限",
        CoreWebView2DownloadInterruptReason.NetworkDisconnected => "网络已断开",
        CoreWebView2DownloadInterruptReason.NetworkTimeout => "网络连接超时",
        CoreWebView2DownloadInterruptReason.ServerForbidden => "服务器拒绝下载",
        _ => reason.ToString()
    };
}
