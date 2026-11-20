using System;
using Microsoft.UI.Xaml;

namespace StealthWorkspace.Models;

public class CookieDisplayItem
{
    public string Name { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Path { get; set; } = "/";
    public bool IsSecure { get; set; }
    public bool IsHttpOnly { get; set; }

    public Visibility SecureBadgeVisibility => IsSecure ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HttpOnlyBadgeVisibility => IsHttpOnly ? Visibility.Visible : Visibility.Collapsed;
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
