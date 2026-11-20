using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using StealthWorkspace.Controls;
using StealthWorkspace.Models;
using StealthWorkspace.Services;
using Windows.System;

namespace StealthWorkspace.Pages;

public sealed partial class BrowserPage
{
    private bool _tabSearchOpen;
    private bool _tabRefreshQueued;
    private readonly List<BrowserTabSearchItem> _tabSearchRows = new();
    private readonly FontIconSource _closedTabIcon = new() { Glyph = "\uE81C" };

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
        TabSearchScope.SelectedItem = OpenTabsSection;
        TabSearchBox.Text = "";
        _tabSearchOpen = true;
        RefreshTabList();
    }

    private void TabListFlyout_Opened(object sender, object args) => TabSearchBox.Focus(FocusState.Programmatic);
    private void TabListFlyout_Closed(object sender, object args) => ClearTabSearch();
    private void TabSearchBox_TextChanged(object sender, TextChangedEventArgs args) { if (_tabSearchOpen) FilterTabList(); }
    private void TabSearchScope_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_tabSearchOpen) return;
        RefreshTabList();
    }

    private void ClearTabSearch()
    {
        _tabSearchOpen = false;
        TabSearchResults.ItemsSource = null;
        _tabSearchRows.Clear();
        TabSearchBox.Text = "";
    }

    private void CloseTabSearch() { TabListFlyout.Hide(); ClearTabSearch(); }

    private void ScheduleOpenTabRefresh()
    {
        if (!_tabSearchOpen || TabSearchScope.SelectedItem != OpenTabsSection || _tabRefreshQueued) return;
        _tabRefreshQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _tabRefreshQueued = false;
            if (_tabSearchOpen && TabSearchScope.SelectedItem == OpenTabsSection) RefreshTabList();
        });
    }

    private void RefreshTabList()
    {
        if (TabSearchResults == null || TabSearchBox == null) return;
        _tabSearchRows.Clear();
        bool recent = TabSearchScope.SelectedItem == ClosedTabsSection;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TabSearchResults, recent ? "最近关闭的标签页" : "打开的标签页");
        if (recent)
        {
            foreach (var closed in BrowserSessionHistory.RecentlyClosed)
                _tabSearchRows.Add(new BrowserTabSearchItem(closed, _closedTabIcon));
        }
        else
        {
            foreach (var item in BrowserTabView.TabItems.OfType<TabViewItem>())
            {
                if (item.Content is not BrowserTabControl tab) continue;
                string title = item.Header?.ToString() ?? "新标签页";
                string address = tab.CurrentUrl;
                _tabSearchRows.Add(new BrowserTabSearchItem(item, title,
                    string.IsNullOrEmpty(address) || address == "about:blank" ? "尚未打开网页" : address, item.IconSource));
            }
        }
        FilterTabList();
    }

    private void FilterTabList()
    {
        // Typing filters the flyout's in-memory snapshot. Reading CoreWebView2.Source
        // and constructing a native IconSource for every tab on each keystroke is costly.
        var previous = TabSearchResults.SelectedItem as BrowserTabSearchItem;
        string query = TabSearchBox.Text.Trim();
        var matches = _tabSearchRows.Where(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            item.Address.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        TabSearchResults.ItemsSource = matches;
        TabSearchResults.SelectedItem = matches.FirstOrDefault(item =>
            item.Tab != null && ReferenceEquals(item.Tab, previous?.Tab) || item.Closed != null && ReferenceEquals(item.Closed, previous?.Closed))
            ?? matches.FirstOrDefault(item => ReferenceEquals(item.Tab, BrowserTabView.SelectedItem)) ?? matches.FirstOrDefault();
        TabSearchEmpty.Text = query.Length == 0 && TabSearchScope.SelectedItem == ClosedTabsSection ? "本次会话还没有关闭的网页" : "没有匹配的标签页";
        UiMotion.SetVisible(TabSearchEmpty, matches.Length == 0, MotionCue.Fade);
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
        if (item.Closed != null) ReopenClosedTab(item.Closed);
        else if (item.Tab != null && BrowserTabView.TabItems.Contains(item.Tab)) BrowserTabView.SelectedItem = item.Tab;
        else return;
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
