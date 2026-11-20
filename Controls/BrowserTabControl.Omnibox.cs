using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using StealthWorkspace.Models;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private BrowserAddressSuggestions.EditSession? _addressEdit;

    private static TextBox? FindAddressEditor(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox editor) return editor;
            if (FindAddressEditor(child) is { } nested) return nested;
        }
        return null;
    }

    private void SelectAddressText() => FindAddressEditor(UrlTextBox)?.SelectAll();

    private void AddressTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_isDisposed || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        // No request leaves the computer while typing. Enter alone commits a
        // search/navigation; arrows select only local bookmarks/session history.
        var bookmarks = SettingsService.Instance.Data.Bookmarks;
        if (bookmarks.Count >= 256) _addressEdit ??= new();
        sender.ItemsSource = BrowserAddressSuggestions.Get(sender.Text, bookmarks,
            BrowserSessionHistory.Items, SettingsService.Instance.GetSearchUrl, _addressEdit);
    }

    private void AddressLostFocus(object sender, RoutedEventArgs args) => _addressEdit = null;

    private void AddressQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _addressEdit = null;
        sender.IsSuggestionListOpen = false;
        string input = args.ChosenSuggestion is BrowserAddressSuggestion suggestion ? suggestion.Target : args.QueryText;
        SubmitAddress(input);
    }

    private void AddressSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is BrowserAddressSuggestion suggestion) sender.Text = suggestion.Input;
    }
}
