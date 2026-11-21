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
    private ulong? _edgeDocumentAfterMenuClose;

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
        ShowExternalActionStatus(severity, title, message);
        return result;
    }

    private void ShowExternalActionStatus(InfoBarSeverity severity, string title, string message)
    {
        BrowserErrorBar.Severity = severity;
        BrowserErrorBar.ActionButton.Visibility = Visibility.Collapsed;
        BrowserErrorBar.Title = title;
        BrowserErrorBar.Message = message;
        UiMotion.Open(BrowserErrorBar);
        SetHubStatus(message);
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
        _edgeDocumentAfterMenuClose = _imageDocumentId;
        MoreActionsFlyout.Hide();
    }

    private async Task OpenInEdgeAsync(ulong document)
    {
        if (_isDisposed || !IsLoaded || !IsForegroundTab || document != _imageDocumentId || !BrowserDownloadPath.IsCurrentWorkspace(_workspace)) return;
        try
        {
            // Only hand off a committed or submitted address. Never export text
            // still being edited, cookies, a WebView profile, or an internal URL.
            string? address = _navigationFailed || _isInitializing ? _lastRequestedUrl : CurrentUrl;
            if (!TryCreateWebUri(address, out var uri)) uri = null;
            if (uri != null && !string.IsNullOrEmpty(uri.UserInfo))
            {
                ShowExternalActionStatus(InfoBarSeverity.Warning, "网址含有账号信息", "请先打开不含用户名和密码的网址，再交给 Edge。");
                return;
            }
            const string policy = @"SOFTWARE\Policies\Microsoft\Edge";
            if (Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\" + policy, "InPrivateModeAvailability", null) is 1 ||
                Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\" + policy, "InPrivateModeAvailability", null) is 1)
            {
                ShowExternalActionStatus(InfoBarSeverity.Warning, "Edge 隐私窗口不可用", "此电脑的 Edge 策略已关闭 InPrivate，未向 Edge 传送网址。");
                return;
            }
            string? executable = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData }
                .Select(folder => System.IO.Path.Combine(Environment.GetFolderPath(folder), "Microsoft", "Edge", "Application", "msedge.exe"))
                .FirstOrDefault(System.IO.File.Exists);
            if (executable == null) { ShowExternalActionStatus(InfoBarSeverity.Warning, "未找到 Microsoft Edge", "请确认本机已安装 Edge。"); return; }
            if (CleanerAccess.IsRestricted)
            {
                await ShowEdgeInPrivateInstructionsAsync(uri);
                return;
            }
            var launch = new ProcessStartInfo(executable) { UseShellExecute = false };
            launch.ArgumentList.Add("--inprivate");
            launch.ArgumentList.Add("--new-window");
            ConfigureEdgeAcceptanceProfile(launch);
            if (uri != null) launch.ArgumentList.Add(uri.AbsoluteUri);
            Process.Start(launch)?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Open Edge InPrivate failed: " + ex.GetType().Name);
            ShowExternalActionStatus(InfoBarSeverity.Error, "无法打开 Edge 隐私窗口", "请在 Edge 中按 Ctrl+Shift+N，或检查此电脑的 Edge 设置。");
        }
    }

    private async Task ShowEdgeInPrivateInstructionsAsync(Uri? address)
    {
        ulong document = _imageDocumentId;
        const string directions = "当前 Windows 权限限制了 Stealth 直接打开 Edge。请在 Edge 中按 Ctrl+Shift+N，或右键任务栏的 Edge 图标，选择「新建 InPrivate 窗口」。";
        string steps = address == null ? directions : directions + "\n\n选择下方按钮复制当前网页地址，到 InPrivate 窗口按 Ctrl+L，再粘贴并打开。";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "在 Edge 隐私窗口打开",
            Content = steps,
            PrimaryButtonText = address == null ? string.Empty : "复制当前网页地址",
            CloseButtonText = address == null ? "知道了" : "取消",
            DefaultButton = ContentDialogButton.Close
        };
        bool copied = false;
        bool IsCurrent() => !_isDisposed && IsForegroundTab && document == _imageDocumentId && BrowserDownloadPath.IsCurrentWorkspace(_workspace);
        if (address != null)
        {
            var instructions = new TextBlock { Text = steps, TextWrapping = TextWrapping.Wrap };
            var addressBox = new TextBox { Name = "EdgeHandoffAddress", Header = "当前网页地址", IsReadOnly = true, Text = address.AbsoluteUri };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(instructions);
            panel.Children.Add(addressBox);
            panel.Children.Add(error);
            dialog.Content = panel;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (!IsCurrent()) return;
                copied = BrowserShareLink.TryWritePrivateClipboard(address.AbsoluteUri, _shareLinkWriter);
                if (copied) return;
                args.Cancel = true;
                error.Text = "Windows 未允许自动复制。网址已选中，可按 Ctrl+C 尝试复制，或在 Edge 地址栏输入。";
                error.Visibility = Visibility.Visible;
                addressBox.Focus(FocusState.Programmatic);
                addressBox.SelectAll();
            };
        }
        if (await WorkspaceDialogs.ShowAsync(dialog) == ContentDialogResult.Primary && copied && IsCurrent())
            ShowExternalActionStatus(InfoBarSeverity.Success, "已复制网页地址", "打开 Edge 的 InPrivate 窗口，在地址栏粘贴即可。请单独关闭该窗口。");
    }
}
