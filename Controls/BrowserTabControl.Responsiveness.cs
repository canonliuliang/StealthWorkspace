using Microsoft.Web.WebView2.Core;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private const string UnresponsiveTitle = "网页暂时没有响应";
    private CancellationTokenSource? _responseProbe;

    private void OnRendererUnresponsive(CoreWebView2 core)
    {
        if (_responseProbe != null || _needsRecreate) return;
        ShowBrowserError(UnresponsiveTitle, "可以继续等待，或点击重试重新加载。重新加载会丢失未提交的网页内容。");
        AppLog.Warn("WebView2 renderer busy; keeping the current document");
        _responseProbe = new CancellationTokenSource();
        _ = ObserveRendererResponseAsync(core, _responseProbe);
    }

    private async Task ObserveRendererResponseAsync(CoreWebView2 core, CancellationTokenSource probe)
    {
        try
        {
            // One queued response, not periodic polling. When a long script
            // finishes, remove only our warning and retain the page/form state.
            await core.ExecuteScriptAsync("0").AsTask().WaitAsync(probe.Token);
            if (!_isDisposed && ReferenceEquals(probe, _responseProbe) && ReferenceEquals(core, _hookedCore))
            {
                if (BrowserErrorBar.Title == UnresponsiveTitle) BrowserErrorBar.IsOpen = false;
                SetHubStatus("网页已恢复响应");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_isDisposed) AppLog.Warn("Renderer response probe: " + ex.GetType().Name); }
        finally
        {
            if (ReferenceEquals(probe, _responseProbe)) _responseProbe = null;
            probe.Dispose();
        }
    }

    private void CancelResponseProbe()
    {
        var probe = _responseProbe;
        _responseProbe = null;
        probe?.Cancel();
        // The observer owns disposal after cancellation is delivered.
    }
}
