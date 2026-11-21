using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using StealthWorkspace.Services;

namespace StealthWorkspace.Controls;

public sealed partial class PrivacyShield : UserControl
{
    public PrivacyShield() => InitializeComponent();

    public bool Compact
    {
        get => Outline.Visibility == Visibility.Visible;
        set
        {
            Outline.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            Artwork.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // Use the same brushes as the real system contrast theme in an isolated visual audit.
    internal void PreviewSystemContrast()
    {
        if (!SmokeTest.UiSmoke || !AppPaths.HasDataRootOverride) throw new InvalidOperationException();
        var palette = (ResourceDictionary)Resources.ThemeDictionaries["HighContrast"];
        Outline.Stroke = (Brush)palette["ShieldBase"];
        Base.Fill = (Brush)palette["ShieldBase"];
        Fold.Fill = (Brush)palette["ShieldFold"];
        Face.Fill = (Brush)palette["ShieldFace"];
        Base.Stroke = (Brush)palette["ShieldRim"];
    }
}
