using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace StealthWorkspace.Pages;

public sealed partial class SettingsPage
{
    private sealed record SettingCard(FrameworkElement View, string Text);
    private readonly List<(FrameworkElement Section, List<SettingCard> Cards)> _searchSections = [];

    private void IndexVisibleSettings()
    {
        // A page can load again after navigation. Undo the old filter before reading
        // the current layout, and exclude controls hidden for the active workspace.
        _searchSections.Clear();
        SettingsSearch.Text = string.Empty;
        SettingsMatches.Visibility = Visibility.Collapsed;
        bool decoy = Services.VaultService.Instance.IsDecoyMode;
        foreach (var section in SettingsSections.Children.OfType<FrameworkElement>())
        {
            if (section == BackupSection)
            {
                if (!decoy) _searchSections.Add((section, [new(section, ReadControlText(section))]));
                continue;
            }
            if (section is not StackPanel group || group == SearchSettingControls || (group == VaultSecuritySection && decoy)) continue;
            group.Visibility = Visibility.Visible;
            string heading = group.Children.Count > 0 ? ReadControlText(group.Children[0]) : "";
            var cards = new List<SettingCard>();
            foreach (var card in group.Children.OfType<Border>())
            {
                if (card == BookmarkTransferSection && decoy) continue;
                card.Visibility = Visibility.Visible;
                cards.Add(new(card, heading + " " + ReadControlText(card)));
            }
            if (cards.Count > 0) _searchSections.Add((group, cards));
        }
    }

    private static string ReadControlText(UIElement element) => element switch
    {
        TextBlock text => text.Text,
        Panel panel => string.Join(" ", panel.Children.Select(ReadControlText)),
        Border { Child: { } child } => ReadControlText(child),
        ContentControl { Content: string label } => label,
        ComboBox combo => string.Join(" ", combo.Items.OfType<ComboBoxItem>().Select(item => item.Content)),
        _ => ""
    };

    private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchSections.Count == 0) return;
        string query = SettingsSearch.Text.Trim();
        int matches = 0;
        foreach (var (section, cards) in _searchSections)
        {
            int visible = 0;
            foreach (var card in cards)
            {
                bool match = card.Text.Contains(query, StringComparison.OrdinalIgnoreCase);
                card.View.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                if (match) visible++;
            }
            section.Visibility = visible != 0 ? Visibility.Visible : Visibility.Collapsed;
            matches += visible;
        }
        SettingsMatches.Text = matches > 0 ? $"找到 {matches} 项设置" : "没有匹配的设置，请换一个关键词。";
        SettingsMatches.Visibility = query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
