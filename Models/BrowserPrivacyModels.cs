using System;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace StealthWorkspace.Models;

public class CookieDisplayItem
{
    public string Name { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Path { get; set; } = "/";
    public string DisplayExpires { get; set; } = "会话结束时";
    public bool IsSecure { get; set; }
    public bool IsHttpOnly { get; set; }

    /// <summary>
    /// The live WebView2 cookie object this row was built from.
    /// Deleting this instance (instead of rebuilding a lossy cookie via
    /// <c>CreateCookie</c>) preserves Secure/HttpOnly/SameSite/Expires, which the
    /// cookie store needs in order to actually match and remove the cookie.
    /// May be null when the row was restored from a stale snapshot.
    /// </summary>
    public CoreWebView2Cookie? NativeCookie { get; set; }

    public Visibility SecureBadgeVisibility => IsSecure ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HttpOnlyBadgeVisibility => IsHttpOnly ? Visibility.Visible : Visibility.Collapsed;
    public string ValuePreview => Value.Length > 28 ? Value.Substring(0, 28) + "..." : Value;
}

public class PermissionRuleItem
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = "\uE72E";
    public bool IsBlocked { get; set; } = true;
    public int BlockedCount { get; set; }
}

public class HistorySessionItem
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime VisitedAt { get; set; } = DateTime.Now;
    public string FormattedTime => VisitedAt.ToString("HH:mm:ss");
}
