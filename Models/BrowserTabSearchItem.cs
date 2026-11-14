using Microsoft.UI.Xaml.Controls;

namespace StealthWorkspace.Models;

public sealed class BrowserTabSearchItem
{
    internal TabViewItem Tab { get; }
    public string Title { get; }
    public string Address { get; }
    public IconSource Icon { get; }
    public string CloseLabel => "关闭标签页 " + Title;
    public override string ToString() => Title + " · " + Address;

    internal BrowserTabSearchItem(TabViewItem tab, string title, string address, IconSource icon)
    {
        Tab = tab; Title = title; Address = address; Icon = icon;
    }
}
