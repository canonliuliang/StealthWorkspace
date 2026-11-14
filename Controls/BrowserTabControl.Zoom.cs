using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private ushort? _zoomAfterMenuClose;
    private int _zoomReadVersion;
    private bool _nativeZoomDispatching;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _zoomPanelTimer;

    private void ZoomInBtn_Click(object sender, RoutedEventArgs e) => ZoomFromButton(0xBB, sender);
    private void ZoomOutBtn_Click(object sender, RoutedEventArgs e) => ZoomFromButton(0xBD, sender);
    private void ZoomResetBtn_Click(object sender, RoutedEventArgs e) => ZoomFromButton(0x30, sender);

    private void ZoomFromButton(ushort key, object sender)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Button button && (button == ZoomPanelPlus || button == ZoomPanelMinus || button == ZoomPanelReset)) _ = SendNativeZoomAsync(key);
        else { _zoomAfterMenuClose = key; MoreActionsFlyout.Hide(); }
    }

    private async Task<bool> SendNativeZoomAsync(ushort key)
    {
        if (SmokeTest.Enabled) SmokeTest.Report($"zoom host: initialized={_isInitialized}; foreground={IsForegroundTab}; loaded={_browserControl?.IsLoaded}; focus={_browserControl?.FocusState}");
        if (_nativeZoomDispatching || _isDisposed || !_isInitialized || !IsForegroundTab || _browserControl is not { } browser) return false;
        _nativeZoomDispatching = true;
        try
        {
            browser.Focus(FocusState.Programmatic);
            // Complete the XAML focus transfer before sending the native shortcut.
            await Task.Yield();
            browser.Focus(FocusState.Programmatic);
            if (_isDisposed || !IsForegroundTab || browser.FocusState == FocusState.Unfocused) return false;
            if (!BrowserZoomInput.Send(MainWindow.CurrentHandle, key)) return false;
            // Input is now queued atomically. Do not discard subsequent quick
            // clicks just because the percentage label has not refreshed yet.
            _nativeZoomDispatching = false;
            bool wasOpen = ZoomPanel.Visibility == Visibility.Visible;
            ZoomPanel.Visibility = Visibility.Visible;
            if (!wasOpen) UiMotion.Enter(ZoomPanel, 0, -3);
            if (_zoomPanelTimer == null)
            {
                _zoomPanelTimer = DispatcherQueue.CreateTimer();
                _zoomPanelTimer.IsRepeating = false;
                _zoomPanelTimer.Interval = TimeSpan.FromSeconds(5);
                _zoomPanelTimer.Tick += ZoomPanelTimer_Tick;
            }
            _zoomPanelTimer.Stop(); _zoomPanelTimer.Start();
            await Task.Delay(100);
            await RefreshNativeZoomAsync();
            return true;
        }
        catch (Exception ex) { if (!_isDisposed) AppLog.Warn("Native browser zoom: " + ex.GetType().Name); return false; }
        finally { _nativeZoomDispatching = false; }
    }

    private async Task RefreshNativeZoomAsync()
    {
        if (_isDisposed || !_isInitialized || _hookedCore is not { } core) return;
        int version = ++_zoomReadVersion;
        try
        {
            string json = await core.ExecuteScriptAsync("window.devicePixelRatio").AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            if (_isDisposed || version != _zoomReadVersion || !ReferenceEquals(core, _hookedCore)) return;
            if (!double.TryParse(json, NumberStyles.Float, CultureInfo.InvariantCulture, out double ratio)) return;
            double factor = ratio / (XamlRoot?.RasterizationScale ?? 1);
            if (!double.IsFinite(factor) || factor is < 0.1 or > 10) return;
            string percent = Math.Round(factor * 100) + "%";
            ZoomPanelReset.Content = percent;
            if (ZoomResetBtn != null)
            {
                ZoomResetBtn.Content = percent;
                AutomationProperties.SetName(ZoomResetBtn, "重置缩放，当前 " + percent);
            }
            AutomationProperties.SetName(ZoomPanelReset, "重置缩放，当前 " + percent);
            if (SmokeTest.Enabled) SmokeTest.Report("native zoom: " + percent);
        }
        catch (Exception ex) { if (!_isDisposed) AppLog.Warn("Read browser zoom: " + ex.GetType().Name); }
    }

    private void HideZoomPanel()
    {
        _zoomPanelTimer?.Stop();
        ZoomPanel.Visibility = Visibility.Collapsed;
    }

    private void CloseZoomPanel_Click(object sender, RoutedEventArgs args) => HideZoomPanel();
    private void ZoomPanelTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => HideZoomPanel();

    private void DetachNativeZoom()
    {
        _zoomAfterMenuClose = null;
        _zoomReadVersion++;
        HideZoomPanel();
        if (_zoomPanelTimer != null) _zoomPanelTimer.Tick -= ZoomPanelTimer_Tick;
        _zoomPanelTimer = null;
    }
}
