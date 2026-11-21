using Microsoft.Web.WebView2.Core;

namespace StealthWorkspace.Services;

// Lives with one tab. A temporary exception only affects requests made by the
// current top-level origin. Subframes of a different origin use the defaults.
internal sealed class BrowserTabPermissions
{
    private readonly Dictionary<CoreWebView2PermissionKind, bool> _overrides = new();
    internal string? Origin { get; private set; }
    internal bool AutomaticPopupsAllowed { get; private set; }

    private static string? OriginOf(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}" : null;

    internal void SetDocument(string? address)
    {
        string? origin = OriginOf(address);
        if (Origin == origin) return;
        _overrides.Clear();
        AutomaticPopupsAllowed = false;
        Origin = origin;
    }

    internal void SetAutomaticPopups(string expectedOrigin, bool allowed)
    {
        if (Origin == expectedOrigin) AutomaticPopupsAllowed = allowed;
    }

    internal bool AllowsAutomaticPopupFrom(string? frameAddress) =>
        AutomaticPopupsAllowed && Origin != null && Origin == OriginOf(frameAddress);

    internal static bool DefaultBlocked(CoreWebView2PermissionKind kind, AppSettingsData defaults) => kind switch
    {
        CoreWebView2PermissionKind.Camera => defaults.BlockCamera,
        CoreWebView2PermissionKind.Microphone => defaults.BlockMicrophone,
        CoreWebView2PermissionKind.Geolocation => defaults.BlockGeolocation,
        CoreWebView2PermissionKind.Notifications => defaults.BlockNotifications,
        _ => true
    };

    internal void SetSiteRule(CoreWebView2PermissionKind kind, bool block, AppSettingsData defaults)
    {
        if (Origin == null || kind is not (CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone or
            CoreWebView2PermissionKind.Geolocation or CoreWebView2PermissionKind.Notifications)) return;
        if (block == DefaultBlocked(kind, defaults)) _overrides.Remove(kind);
        else _overrides[kind] = block;
    }

    internal bool Blocks(CoreWebView2PermissionKind kind, string? requester, AppSettingsData defaults)
    {
        bool sameTopOrigin = Origin != null && Origin == OriginOf(requester);
        if (sameTopOrigin && _overrides.TryGetValue(kind, out bool block)) return block;
        return DefaultBlocked(kind, defaults);
    }

    // These two requests are denied unless the visible tab obtains a one-time
    // confirmation. An ordinary site exception cannot grant filesystem access.
    internal bool CanAskForFilePermission(CoreWebView2PermissionKind kind, string? requester) =>
        kind is (CoreWebView2PermissionKind.MultipleAutomaticDownloads or CoreWebView2PermissionKind.FileReadWrite)
        && Origin != null && Origin == OriginOf(requester);
}
