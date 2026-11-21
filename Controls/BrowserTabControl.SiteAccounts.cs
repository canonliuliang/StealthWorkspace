using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private string PrivacyModeExplanation => _workspace?.Temporary == true
        ? "临时空间使用独立的网站数据，关闭后清除登录和浏览记录。"
        : "退出后清除浏览记录，保留网站登录。已登录的网站仍能认出你。";

    private void UpdateAccountActionAvailability()
    {
        if (SiteCookiesOnlyButton == null || AllCookiesOnlyButton == null || ForgetSiteButton == null || ClearAllAccountsBtn == null)
            return;
        bool canManage = !_isDisposed && !_isInitializing && IsForegroundTab && BrowserDownloadPath.IsCurrentWorkspace(_workspace);
        bool hasWebsite = canManage && _hookedCore is { } core && TryCreateWebUri(core.Source, out _);
        SiteCookiesOnlyButton.IsEnabled = hasWebsite;
        ForgetSiteButton.IsEnabled = hasWebsite;
        AllCookiesOnlyButton.IsEnabled = canManage;
        ClearAllAccountsBtn.IsEnabled = canManage;
    }

    private async Task<CoreWebView2?> GetCoreForProfileActionAsync()
    {
        if (_hookedCore != null) return _hookedCore;
        if (_isInitializing && _initializationTask != null) await _initializationTask;
        else await InitializeAsync(null, keepStartSurface: true);
        if (_isDisposed || !IsForegroundTab ||
            !ReferenceEquals(_workspace, SettingsService.Instance.ActiveBookmarkSession)) return null;
        if (_hookedCore == null) SetHubStatus("浏览器未能启动，网站数据仍保留");
        return _hookedCore;
    }

    private async void ClearCookiesOnly_Click(object sender, RoutedEventArgs args)
    {
        bool allSites = ReferenceEquals(sender, AllCookiesOnlyButton);
        var core = _hookedCore;
        string? host = !allSites && core != null && TryCreateWebUri(core.Source, out var page) ? page?.IdnHost : null;
        if (!allSites && string.IsNullOrEmpty(host)) return;
        ulong document = _imageDocumentId;
        bool Current() => !_isDisposed && IsForegroundTab && ReferenceEquals(core, _hookedCore) &&
            BrowserDownloadPath.IsCurrentWorkspace(_workspace) && (allSites || document == _imageDocumentId);
        try
        {
            string scope = allSites ? "此空间内所有网站" : host!;
            string detail = $"{scope}的 Cookie 会被移除，网站可能要求重新登录。离线文件和网页本地存储将保留。";
            if (!allSites) detail += "共用登录 Cookie 的子域名也可能退出。";
            if (!await ConfirmForCurrentTabAsync(core, allSites ? "仅清除所有网站的 Cookie？" : $"仅清除 {host} 的 Cookie？",
                detail, "清除 Cookie") || !Current()) return;
            core ??= await GetCoreForProfileActionAsync();
            if (core == null || !Current()) return;
            if (allSites) await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.Cookies);
            else await BrowserSiteData.DeleteCookiesForHostAsync(core, host!, Current);
            if (!Current()) return;
            ClearCookieRows();
            SetHubStatus(allSites ? "已清除所有网站的 Cookie，网页本地存储已保留" : "已清除本站 Cookie，网页本地存储已保留");
        }
        catch (Exception ex)
        {
            AppLog.Warn("清除 Cookie 未完成：" + ex.GetType().Name);
            if (Current()) SetHubStatus("Cookie 未全部清除，请重试");
        }
    }

    private async void ForgetSite_Click(object sender, RoutedEventArgs args)
    {
        if (_hookedCore is not { } core || !TryCreateWebUri(core.Source, out var page) || page == null) return;
        ulong document = _imageDocumentId;
        bool SameDocument() => !_isDisposed && _hookedCore == core && _imageDocumentId == document
            && ReferenceEquals(_workspace, SettingsService.Instance.ActiveBookmarkSession);
        try
        {
            if (!await ConfirmSessionDataClearAsync(core, "清除这个网站的账号和数据？",
                $"{page.IdnHost} 的登录和离线内容将移除。与它共用登录凭据的子域名也可能需要重新登录。") || !SameDocument()) return;
            await BrowserSiteData.ClearSiteAccountAsync(core, page, SameDocument);
            if (!SameDocument()) return;
            ClearCookieRows();
            SetHubStatus("已清除该网站的登录和离线数据");
            core.Reload();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn("清除网站账号数据未完成：" + ex.GetType().Name);
            if (SameDocument()) SetHubStatus("网站数据未全部清除，请重试");
        }
    }

    private async void ClearAllAccounts_Click(object sender, RoutedEventArgs args)
    {
        var core = _hookedCore;
        ulong document = _imageDocumentId;
        try
        {
            if (!await ConfirmForCurrentTabAsync(core, "清除所有网站的账号和数据？",
                "此空间内所有网站的登录、离线内容和未同步草稿都会移除。收藏和下载的文件会保留。", "确认清除")
                || !ReferenceEquals(_workspace, SettingsService.Instance.ActiveBookmarkSession)) return;
            if (core == null)
            {
                core = await GetCoreForProfileActionAsync();
                if (core == null || _imageDocumentId != document) return;
            }
            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllSite);
            if (_isDisposed || _hookedCore != core) return;
            ClearCookieRows();
            SetHubStatus("已清除所有网站的登录和离线数据");
            if (_imageDocumentId == document && TryCreateWebUri(core.Source, out _)) core.Reload();
        }
        catch (Exception ex)
        {
            AppLog.Warn("清除所有网站数据未完成：" + ex.GetType().Name);
            if (!_isDisposed && _hookedCore == core) SetHubStatus("网站数据未全部清除，请重试");
        }
    }
}
