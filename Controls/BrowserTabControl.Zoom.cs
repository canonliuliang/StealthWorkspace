using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private static readonly double[] StartZoomSteps = [.5, .67, .75, .8, .9, 1, 1.1, 1.25, 1.5, 1.75, 2];
    private readonly SemaphoreSlim _zoomCommands = new(1);
    private ushort? _zoomAfterMenuClose;
    private bool _nativeZoomDispatching;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _zoomPanelTimer;

    private void ZoomInBtn_Click(object sender, RoutedEventArgs e) => ZoomFromButton(0xBB, sender);
    private void ZoomOutBtn_Click(object sender, RoutedEventArgs e) => ZoomFromButton(0xBD, sender);
    private void ZoomResetBtn_Click(object sender, RoutedEventArgs e) => ZoomFromButton(0x30, sender);

    private void ZoomFromButton(ushort key, object sender)
    {
        if (StartSurface.Visibility == Visibility.Visible || sender is Microsoft.UI.Xaml.Controls.Button button &&
            (button == ZoomPanelPlus || button == ZoomPanelMinus || button == ZoomPanelReset))
            _ = SendNativeZoomAsync(key);
        else
        {
            // Chromium only accepts its own shortcuts once the flyout has released focus.
            _zoomAfterMenuClose = key;
            MoreActionsFlyout.Hide();
        }
    }

    private async Task<bool> SendNativeZoomAsync(ushort key)
    {
        if (_isDisposed || !IsForegroundTab) return false;
        bool start = StartSurface.Visibility == Visibility.Visible;
        var document = _imageDocumentId;
        await _zoomCommands.WaitAsync();
        try
        {
            if (_isDisposed || !IsForegroundTab || start != (StartSurface.Visibility == Visibility.Visible) || document != _imageDocumentId) return false;
            if (start)
            {
                double old = StartPageScroller.ZoomFactor;
                double target = key == 0x30 ? 1 : key == 0xBB
                    ? StartZoomSteps.FirstOrDefault(z => z > old + .01, StartZoomSteps[^1])
                    : StartZoomSteps.LastOrDefault(z => z < old - .01, StartZoomSteps[0]);
                if (Math.Abs(target - old) > .01)
                {
                    StartPageScroller.ChangeView(null, null, (float)target, disableAnimation: true);
                    for (int i = 0; i < 20 && Math.Abs(StartPageScroller.ZoomFactor - target) > .01; i++) await Task.Delay(20);
                }
                UpdateZoomLabel(StartPageScroller.ZoomFactor, true);
                return Math.Abs(StartPageScroller.ZoomFactor - target) < .01;
            }

            if (_browserControl is not { } browser || _hookedCore is not { } core) return false;
            double? before = await ReadPageZoomAsync(core);
            if (!before.HasValue) throw new InvalidOperationException("Zoom level unavailable");
            bool alreadyAtLimit = key == 0x30 && Math.Abs(before.Value - 1) < .01 ||
                key == 0xBB && before >= 4.99 || key == 0xBD && before <= .251;
            if (!alreadyAtLimit)
            {
                _nativeZoomDispatching = true;
                // A XAML Focus call can complete before the flyout restores focus
                // or Chromium receives it. Do not send a chord to another window.
                bool focused = false;
                for (int i = 0; i < 16 && !focused; i++)
                {
                    if (_isDisposed || !IsForegroundTab || core != _hookedCore || document != _imageDocumentId) return false;
                    browser.Focus(FocusState.Programmatic);
                    await Task.Delay(20);
                    focused = BrowserZoomInput.IsOwnerForeground(MainWindow.CurrentHandle) &&
                        await core.ExecuteScriptAsync("document.hasFocus()").AsTask().WaitAsync(TimeSpan.FromMilliseconds(700)) == "true";
                }
                if (!focused || !BrowserZoomInput.Send(MainWindow.CurrentHandle, key)) throw new InvalidOperationException("Browser focus unavailable");
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    await Task.Delay(25);
                    if (_isDisposed || !IsForegroundTab || core != _hookedCore || document != _imageDocumentId) return false;
                    var current = await ReadPageZoomAsync(core);
                    bool applied = current.HasValue && (key == 0x30 ? Math.Abs(current.Value - 1) < .01 :
                        key == 0xBB ? current > before + .005 : current < before - .005);
                    if (applied) { UpdateZoomLabel(current!.Value, false); ShowZoomPanel(); return true; }
                }
                throw new InvalidOperationException("Browser did not apply zoom");
            }
            UpdateZoomLabel(before.Value, false);
            ShowZoomPanel();
            return true;
        }
        catch (Exception ex)
        {
            if (SmokeTest.BrowserAudit)
                SmokeTest.Report($"zoom command failed: {ex.GetType().Name}; foreground={BrowserZoomInput.IsOwnerForeground(MainWindow.CurrentHandle)}; tab={IsForegroundTab}; viewFocus={_browserControl?.FocusState}");
            if (!_isDisposed && IsForegroundTab && document == _imageDocumentId)
            {
                AppLog.Warn("Browser zoom: " + ex.GetType().Name);
                ShowBrowserError("缩放未生效", "请点一下当前页面后重试。");
            }
            return false;
        }
        finally { _nativeZoomDispatching = false; _zoomCommands.Release(); }
    }

    private async Task<double?> ReadPageZoomAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        // The engine's CSS-to-DIP factor is independent of display scaling and
        // of scripts that redefine window.devicePixelRatio on the current page.
        string metrics = await core.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}").AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        using var json = JsonDocument.Parse(metrics);
        if (json.RootElement.TryGetProperty("cssVisualViewport", out var viewport) && viewport.TryGetProperty("zoom", out var value) &&
            value.TryGetDouble(out double zoom) && zoom is >= .1 and <= 10) return zoom;
        string ratio = await core.ExecuteScriptAsync("window.devicePixelRatio").AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        if (!double.TryParse(ratio, NumberStyles.Float, CultureInfo.InvariantCulture, out double dpr)) return null;
        double factor = dpr / (XamlRoot?.RasterizationScale ?? 1);
        return factor is >= .1 and <= 10 ? factor : null;
    }

    private async Task RefreshNativeZoomAsync()
    {
        if (_isDisposed) return;
        if (StartSurface.Visibility == Visibility.Visible) { UpdateZoomLabel(StartPageScroller.ZoomFactor, true); return; }
        if (_hookedCore is not { } core) return;
        try
        {
            var factor = await ReadPageZoomAsync(core);
            if (!_isDisposed && core == _hookedCore && StartSurface.Visibility != Visibility.Visible && factor.HasValue)
                UpdateZoomLabel(factor.Value, false);
        }
        catch (Exception ex) { if (!_isDisposed) AppLog.Warn("Read browser zoom: " + ex.GetType().Name); }
    }

    private void UpdateZoomLabel(double factor, bool start)
    {
        string percent = Math.Round(factor * 100) + "%";
        ZoomPanelReset.Content = percent;
        AutomationProperties.SetName(ZoomPanelReset, "重置缩放，当前 " + percent);
        if (ZoomResetBtn == null) return;
        ZoomResetBtn.Content = percent;
        AutomationProperties.SetName(ZoomResetBtn, "重置缩放，当前 " + percent);
        ZoomMenuMinus.IsEnabled = factor > (start ? .501 : .251);
        ZoomMenuPlus.IsEnabled = factor < (start ? 1.99 : 4.99);
    }

    private void ShowZoomPanel()
    {
        UiMotion.SetOverlayVisible(ZoomPanel, true, MotionCue.FromAbove);
        if (_zoomPanelTimer == null)
        {
            _zoomPanelTimer = DispatcherQueue.CreateTimer();
            _zoomPanelTimer.IsRepeating = false;
            _zoomPanelTimer.Interval = TimeSpan.FromSeconds(5);
            _zoomPanelTimer.Tick += ZoomPanelTimer_Tick;
        }
        _zoomPanelTimer.Stop(); _zoomPanelTimer.Start();
    }

    private void HideZoomPanel(bool animate = false)
    {
        _zoomPanelTimer?.Stop();
        if (animate) UiMotion.SetOverlayVisible(ZoomPanel, false, MotionCue.FromAbove);
        else UiMotion.SetVisible(ZoomPanel, false);
    }

    private void CloseZoomPanel_Click(object sender, RoutedEventArgs args) => HideZoomPanel(animate: true);
    private void ZoomPanelTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => HideZoomPanel(animate: true);

    private void DetachNativeZoom()
    {
        _zoomAfterMenuClose = null;
        HideZoomPanel();
        if (_zoomPanelTimer != null) _zoomPanelTimer.Tick -= ZoomPanelTimer_Tick;
        _zoomPanelTimer = null;
    }
}
