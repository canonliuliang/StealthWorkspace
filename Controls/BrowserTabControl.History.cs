using System.Collections.Specialized;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using StealthWorkspace.Models;
using Windows.System;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private bool _focusHistoryOnOpen;
    private bool _historyAfterMenuClose;

    private void PrivacyFlyout_Opened(object sender, object args)
    {
        if (!_focusHistoryOnOpen) return;
        _focusHistoryOnOpen = false;
        if (!_isDisposed && IsForegroundTab && PrivacySections.SelectedItem == HistorySection)
            HistorySearchBox.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
    }

    private void SetHistorySearchActive(bool active)
    {
        _sessionHistory.CollectionChanged -= SessionHistory_CollectionChanged;
        if (HistorySearchBox == null || HistoryListView == null) return;
        if (active) _sessionHistory.CollectionChanged += SessionHistory_CollectionChanged;
        else HistorySearchBox.Text = "";
        FilterSessionHistory();
    }

    private void SessionHistory_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => FilterSessionHistory();
    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs args) => FilterSessionHistory();

    private void FilterSessionHistory()
    {
        if (HistoryListView == null || HistorySearchBox == null || HistoryEmptyText == null) return;
        string query = HistorySearchBox.Text.Trim();
        HistoryListView.ItemsSource = query.Length == 0 ? _sessionHistory :
            _sessionHistory.Where(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Url.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        HistoryEmptyText.Text = query.Length == 0 ? "暂无内存会话历史" : "没有匹配的浏览记录";
        UpdateEmptyNotices();
    }

    private void HistorySearchBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || HistoryListView.Items.FirstOrDefault() is not HistorySessionItem first) return;
        args.Handled = true;
        PrivacyFlyout.Hide();
        NavigateToText(first.Url);
    }
}
