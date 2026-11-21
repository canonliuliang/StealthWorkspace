using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using StealthWorkspace.Services;

namespace StealthWorkspace.Models;

public sealed class BrowserTabSearchItem
{
    internal TabViewItem? Tab { get; }
    internal ClosedBrowserTab? Closed { get; }
    public string Title { get; }
    public string Address { get; }
    public IconSource Icon { get; }
    public string CloseLabel => "关闭标签页 " + Title;
    public Visibility CloseVisibility { get; }
    public override string ToString() => Title + " · " + Address;

    internal BrowserTabSearchItem(TabViewItem tab, string title, string address, IconSource icon, bool canClose)
    {
        Tab = tab; Title = title; Address = address; Icon = icon;
        CloseVisibility = canClose ? Visibility.Visible : Visibility.Collapsed;
    }

    internal BrowserTabSearchItem(ClosedBrowserTab closed, IconSource icon)
    {
        Closed = closed; Title = closed.Title; Address = closed.Url; Icon = icon;
        CloseVisibility = Visibility.Collapsed;
    }
}
