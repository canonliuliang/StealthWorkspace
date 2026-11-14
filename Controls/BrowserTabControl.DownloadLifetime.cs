namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    internal void RetainOnlyForDownloads()
    {
        _retainedForDownloads = true;
        SetTabSelected(false);
        TitleChanged = null;
        AudioPlayingChanged = null;
        NavigationVisualChanged = null;
        NewTabRequested = null;
        PopupRequested = null;
        CloseRequested = null;
        FullScreenModeChanged = null;
        _downloadsFlyout?.Hide();
        _downloadsFlyout = null;
        // Stop the closed page's scripts/media while retaining only the controller
        // that delivers download events. A blank page adds no ongoing page work.
        try { _hookedCore?.Navigate("about:blank"); }
        catch (Exception ex) { Services.AppLog.Warn("Retire download owner: " + ex.GetType().Name); }
    }
}
