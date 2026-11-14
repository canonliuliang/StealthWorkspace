using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using StealthWorkspace.Controls;
using StealthWorkspace.Models;
using Windows.System;

namespace StealthWorkspace.Pages;

public sealed partial class BrowserPage
{
    private bool _tabSearchOpen;

    private void BrowserPage_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        // TextBox consumes Ctrl+Shift+A as Select All before a parent accelerator.
        // Handle only this exact browser command; leave normal editing shortcuts alone.
        if (args.Key != VirtualKey.A || !_isPageLoaded || _isUnloading || _isBurningAll) return;
        bool Down(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!Down(VirtualKey.Control) || !Down(VirtualKey.Shift) || Down(VirtualKey.Menu)) return;
        args.Handled = true;
        if (!_tabSearchOpen) TabListFlyout.ShowAt(TabSearchButton);
        else TabSearchBox.Focus(FocusState.Keyboard);
    }

    private void SearchTabsAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_isPageLoaded && !_isUnloading && !_isBurningAll) TabListFlyout.ShowAt(TabSearchButton);
    }

    private void TabListFlyout_Opening(object sender, object args)
    {
        _tabSearchOpen = true;
        TabSearchBox.Text = "";
        RefreshTabList();
    }

    private void TabListFlyout_Opened(object sender, object args) => TabSearchBox.Focus(FocusState.Programmatic);
    private void TabListFlyout_Closed(object sender, object args) => ClearTabSearch();
    private void TabSearchBox_TextChanged(object sender, TextChangedEventArgs args) { if (_tabSearchOpen) RefreshTabList(); }

    private void ClearTabSearch()
    {
        _tabSearchOpen = false;
        TabSearchResults.ItemsSource = null;
        TabSearchBox.Text = "";
    }

    private void CloseTabSearch() { TabListFlyout.Hide(); ClearTabSearch(); }

    private void RefreshTabList()
    {
        if (TabSearchResults == null || TabSearchBox == null) return;
        var previous = (TabSearchResults.SelectedItem as BrowserTabSearchItem)?.Tab;
        string query = TabSearchBox.Text.Trim();
        var matches = new List<BrowserTabSearchItem>();
        foreach (var item in BrowserTabView.TabItems.OfType<TabViewItem>())
        {
            if (item.Content is not BrowserTabControl tab) continue;
            string title = item.Header?.ToString() ?? "新标签页";
            string address = tab.CurrentUrl;
            if (!title.Contains(query, StringComparison.OrdinalIgnoreCase) && !address.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            matches.Add(new BrowserTabSearchItem(item, title, string.IsNullOrEmpty(address) || address == "about:blank" ? "尚未打开网页" : address, tab.TabIcon));
        }
        // ListView owns scrolling and virtualizes rows; no outer ScrollViewer.
        TabSearchResults.ItemsSource = matches;
        TabSearchResults.SelectedItem = matches.FirstOrDefault(item => ReferenceEquals(item.Tab, previous))
            ?? matches.FirstOrDefault(item => ReferenceEquals(item.Tab, BrowserTabView.SelectedItem)) ?? matches.FirstOrDefault();
        TabSearchEmpty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ReopenClosedButton.IsEnabled = _closedTabs.Count > 0;
    }

    private void TabSearchBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter && TabSearchResults.SelectedItem is BrowserTabSearchItem selected)
        {
            ActivateSearchTab(selected);
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Down && TabSearchResults.SelectedIndex >= 0)
        {
            TabSearchResults.ScrollIntoView(TabSearchResults.SelectedItem);
            if (TabSearchResults.ContainerFromIndex(TabSearchResults.SelectedIndex) is ListViewItem item) item.Focus(FocusState.Keyboard);
            else TabSearchResults.Focus(FocusState.Keyboard);
            args.Handled = true;
        }
    }

    private void ActivateSearchTab(BrowserTabSearchItem item)
    {
        if (!BrowserTabView.TabItems.Contains(item.Tab)) return;
        BrowserTabView.SelectedItem = item.Tab;
        TabListFlyout.Hide();
    }

    private void TabSearchResults_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is BrowserTabSearchItem item) ActivateSearchTab(item);
    }

    private async void CloseSearchTab_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not BrowserTabSearchItem item) return;
        await CloseTabAsync(item.Tab);
    }
}
