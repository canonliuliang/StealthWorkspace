using Microsoft.UI.Xaml;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    internal void ExitPageFullScreen()
    {
        if (!_isBrowserFullScreen) return;
        _isBrowserFullScreen = false;
        TopNavBar.Visibility = Visibility.Visible;
        FullScreenModeChanged?.Invoke(false);
        _ = ExitPageFullScreenAsync();
    }

    private async Task ExitPageFullScreenAsync()
    {
        try
        {
            if (_hookedCore is { } core)
                await core.ExecuteScriptAsync("if(document.fullscreenElement)document.exitFullscreen().catch(()=>{});");
        }
        catch (Exception ex)
        {
            if (!_isDisposed) AppLog.Warn("Exit page fullscreen: " + ex.GetType().Name);
        }
    }
}
