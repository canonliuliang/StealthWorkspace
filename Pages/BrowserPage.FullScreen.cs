using Microsoft.UI.Xaml;
using StealthWorkspace.Services;

namespace StealthWorkspace.Pages;

public sealed partial class BrowserPage
{
    private void RefreshPageFullScreen()
    {
        bool fullScreen = _isPageLoaded && !_isUnloading && !_isBurningAll &&
            _selectedTab?.IsPageFullScreen == true;
        if (_browserFullScreen == fullScreen) return;
        _browserFullScreen = fullScreen;
        // Measure the actual native tab strip, including system text scaling,
        // instead of clipping the page by a hard-coded 42 logical pixels.
        double stripHeight = fullScreen && _selectedTab != null
            ? _selectedTab.TransformToVisual(BrowserTabView).TransformPoint(default).Y : 0;
        BrowserTabView.Margin = fullScreen ? new Thickness(0, -Math.Max(0, stripHeight), 0, 0) : new Thickness(0);
        try { MainWindow.Current?.SetFullScreenMode(fullScreen); }
        catch (Exception ex) { AppLog.Warn("Update page fullscreen: " + ex.GetType().Name); }
    }
}
