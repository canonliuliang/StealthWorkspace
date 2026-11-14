using Microsoft.UI.Xaml;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private bool _hardReloadPending;

    private async Task ReloadIgnoringCacheAsync()
    {
        if (_isDisposed || _hardReloadPending) return;
        if (!_isInitialized || _hookedCore is not { } core || _needsRecreate)
        {
            ReloadCurrentPage();
            return;
        }
        _hardReloadPending = true;
        var navigation = _activeNavigationId;
        string source = core.Source;
        try
        {
            ResumeBrowser();
            // This is a one-shot reload. Do not permanently disable the cache or
            // clear the profile: login cookies and other tabs must survive.
            await core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Bypass-cache reload unavailable: " + ex.GetType().Name);
            if (!_isDisposed && ReferenceEquals(core, _hookedCore) &&
                navigation == _activeNavigationId && source == core.Source)
            {
                SetHubStatus("无法忽略缓存，已改为普通刷新");
                ReloadCurrentPage();
            }
        }
        finally { _hardReloadPending = false; }
    }

    private async void HardReload_Click(object sender, RoutedEventArgs args)
    {
        MoreActionsFlyout.Hide();
        await ReloadIgnoringCacheAsync();
    }
}
