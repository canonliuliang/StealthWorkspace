using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Controls;
using System.Numerics;

namespace StealthWorkspace.Services;

internal static class UiMotion
{
    private static readonly Windows.UI.ViewManagement.UISettings Settings = new();
    internal static bool AnimationsEnabled => Settings.AnimationsEnabled;

    // Composition transforms do not cause layout work. Restart, never queue, when
    // users switch quickly. Native buttons, menus and dialogs retain Fluent motion.
    internal static void Enter(UIElement element, float horizontal = 0, float vertical = 4)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        if (!AnimationsEnabled) return;
        using var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = TimeSpan.FromMilliseconds(140);
        fade.InsertKeyFrame(0, 0.7f);
        fade.InsertKeyFrame(1, 1f);
        visual.StartAnimation("Opacity", fade);
        if (horizontal == 0 && vertical == 0) return;
        using var move = visual.Compositor.CreateVector3KeyFrameAnimation();
        move.Duration = TimeSpan.FromMilliseconds(160);
        using var ease = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1), new Vector2(0.3f, 1));
        move.InsertKeyFrame(0, new Vector3(horizontal, vertical, 0));
        move.InsertKeyFrame(1, Vector3.Zero, ease);
        visual.StartAnimation("Translation", move);
    }

    internal static void Open(InfoBar bar)
    {
        bool wasOpen = bar.IsOpen;
        bar.IsOpen = true;
        if (!wasOpen) Enter(bar);
    }
}
