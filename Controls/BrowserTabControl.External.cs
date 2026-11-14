using System.Diagnostics;
using Microsoft.UI.Xaml;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private void OpenInEdge_Click(object sender, RoutedEventArgs e)
    {
        MoreActionsFlyout.Hide();
        if (!TryCreateWebUri(CurrentUrl, out var uri) || uri == null)
        {
            ShowBrowserError("尚未打开网页", "先输入网址，再使用 Edge 隐私窗口打开。");
            return;
        }
        try
        {
            string? executable = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData }
                .Select(folder => System.IO.Path.Combine(Environment.GetFolderPath(folder), "Microsoft", "Edge", "Application", "msedge.exe"))
                .FirstOrDefault(System.IO.File.Exists);
            if (executable == null) { ShowBrowserError("未找到 Microsoft Edge", "请确认本机已安装 Edge。"); return; }
            var launch = new ProcessStartInfo(executable) { UseShellExecute = true };
            launch.ArgumentList.Add("--inprivate");
            launch.ArgumentList.Add("--new-window");
            launch.ArgumentList.Add(uri.AbsoluteUri);
            Process.Start(launch)?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Open Edge InPrivate failed: " + ex.GetType().Name);
            ShowBrowserError("无法打开 Edge", "请稍后重试。");
        }
    }
}
