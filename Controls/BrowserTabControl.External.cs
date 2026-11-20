using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using StealthWorkspace.Services;
using Windows.ApplicationModel.DataTransfer;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private Func<DataPackage, ClipboardContentOptions, bool> _shareLinkWriter = Clipboard.SetContentWithOptions;

    private void CopyCleanLink_Click(object sender, RoutedEventArgs e) => CopyCleanLink(_shareLinkWriter);

    private ShareLinkResult CopyCleanLink(Func<DataPackage, ClipboardContentOptions, bool> write)
    {
        MoreActionsFlyout.Hide();
        string? source = null;
        try { if (!_isDisposed && _documentReady && VaultService.Instance.IsUnlocked) source = _hookedCore?.Source; }
        catch (Exception ex) { AppLog.Warn("读取已打开网页失败: " + ex.GetType().Name); }
        var result = BrowserShareLink.Copy(source, write);
        if (_isDisposed) return result;
        var (severity, title, message) = result switch
        {
            ShareLinkResult.Cleaned => (InfoBarSeverity.Success, "已复制净化链接", "已移除已知跟踪参数。"),
            ShareLinkResult.Unchanged => (InfoBarSeverity.Success, "已复制网页链接", "网址中没有可移除的已知跟踪参数。"),
            ShareLinkResult.Sensitive => (InfoBarSeverity.Warning, "未复制此链接", "网址可能包含登录信息、支付信息或访问签名。请确认安全后自行决定是否从地址栏复制。"),
            ShareLinkResult.ClipboardBusy => (InfoBarSeverity.Warning, "复制未完成", "Windows 剪贴板暂不可用，请稍后重试。"),
            _ => (InfoBarSeverity.Informational, "尚无可复制的网址", "请先打开 HTTP 或 HTTPS 网页，待页面就绪后再试。")
        };
        BrowserErrorBar.Severity = severity;
        BrowserErrorBar.ActionButton.Visibility = Visibility.Collapsed;
        BrowserErrorBar.Title = title;
        BrowserErrorBar.Message = message;
        UiMotion.Open(BrowserErrorBar);
        SetHubStatus(message);
        return result;
    }

    internal async Task CheckShareLinkWorkflowAsync()
    {
        if (!AppPaths.HasDataRootOverride) throw new InvalidOperationException("Synthetic share-link workspace required");
        await using var site = new BrowserHttpFixture();
        string page = site.Address + "/page?id=1&utm_source=test&id=2#section";
        await NavigateAuditAsync(page);
        string originalEditor = UrlTextBox.Text;
        MoreActionsFlyout.ShowAt(BrowserMenuBtn);
        for (int i = 0; i < 80 && CopyCleanLinkBtn?.IsLoaded != true; i++) await Task.Delay(25);
        if (CopyCleanLinkBtn?.IsLoaded != true || !CopyCleanLinkBtn.IsEnabled) throw new InvalidOperationException("Share-link menu button missing");
        var copied = new TaskCompletionSource<DataPackage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _shareLinkWriter = (package, options) =>
        {
            if (options.IsAllowedInHistory || options.IsRoamable) copied.TrySetException(new InvalidOperationException("Unexpected clipboard options"));
            else copied.TrySetResult(package);
            return true;
        };
        try
        {
            var peer = new ButtonAutomationPeer(CopyCleanLinkBtn);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            var written = await copied.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (await written.GetView().GetTextAsync() != site.Address + "/page?id=1&id=2#section" ||
                GetCurrentDocumentUrl() != page || UrlTextBox.Text != originalEditor || BrowserErrorBar.Severity != InfoBarSeverity.Success)
                throw new InvalidOperationException("Share-link menu command changed the page or copied incorrect text");
        }
        finally
        {
            // A delayed UIA invocation on this isolated audit tab must never
            // fall through to the user's system clipboard after a timeout.
            _shareLinkWriter = static (_, _) => false;
        }
        SmokeTest.Report("audit: share-link menu prepared private clipboard data and preserved navigation and address text");
    }

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
            // .NET 8 supports ArgumentList with either mode. A known executable needs no shell association.
            var launch = new ProcessStartInfo(executable) { UseShellExecute = false };
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
