using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace StealthWorkspace.Services;

internal enum MotionCue { Fade, Inline, FromAbove, FromBelow, FromLeft, FromRight, Page }

// All distances are post-layout DIPs. Each compositor shares immutable animation
// templates. Starting a template on a visual creates an independent animation.
internal static class UiMotion
{
    private static readonly UISettings Settings = new();
    private static readonly ConditionalWeakTable<Compositor, MotionResources> Resources = new();
    private static readonly Dictionary<FrameworkElement, OverlayExit> PendingExits = new();
    internal static bool AnimationsEnabled => Settings.AnimationsEnabled && !SmokeTest.ReduceMotion;

    private sealed record OverlayExit(CompositionScopedBatch Batch, bool WasHitTestVisible);

    private sealed class MotionResources
    {
        private readonly Compositor _compositor;
        private readonly CubicBezierEasingFunction _decelerate;
        private readonly Dictionary<MotionCue, Vector3KeyFrameAnimation> _moves = new();
        internal ScalarKeyFrameAnimation FadeIn { get; }
        internal ScalarKeyFrameAnimation FadeOut { get; }

        internal MotionResources(Compositor compositor)
        {
            _compositor = compositor;
            _decelerate = compositor.CreateCubicBezierEasingFunction(Vector2.Zero, new Vector2(0, 1));
            FadeIn = compositor.CreateScalarKeyFrameAnimation();
            FadeIn.Duration = TimeSpan.FromMilliseconds(167);
            FadeIn.InsertKeyFrame(0, 0.58f);
            FadeIn.InsertKeyFrame(1, 1, _decelerate);

            FadeOut = compositor.CreateScalarKeyFrameAnimation();
            FadeOut.Duration = TimeSpan.FromMilliseconds(83);
            using var accelerate = compositor.CreateCubicBezierEasingFunction(new Vector2(1, 0), Vector2.One);
            FadeOut.InsertExpressionKeyFrame(0, "this.StartingValue");
            FadeOut.InsertKeyFrame(1, 0, accelerate);
        }

        internal Vector3KeyFrameAnimation Move(MotionCue cue)
        {
            if (_moves.TryGetValue(cue, out var cached)) return cached;
            var offset = cue switch
            {
                MotionCue.FromAbove => new Vector3(0, -8, 0),
                MotionCue.FromBelow => new Vector3(0, 8, 0),
                MotionCue.FromLeft => new Vector3(-12, 0, 0),
                MotionCue.FromRight => new Vector3(12, 0, 0),
                MotionCue.Page => new Vector3(0, 16, 0),
                _ => new Vector3(0, 4, 0)
            };
            var move = _compositor.CreateVector3KeyFrameAnimation();
            move.Duration = TimeSpan.FromMilliseconds(cue == MotionCue.Page ? 250 : 167);
            move.InsertKeyFrame(0, offset);
            move.InsertKeyFrame(1, Vector3.Zero, _decelerate);
            _moves.Add(cue, move);
            return move;
        }
    }

    // Use on content surfaces owned by the app, leaving the control template's
    // focus, press and selection states and native scroll manipulation alone.
    internal static void Reveal(UIElement element, MotionCue cue = MotionCue.Inline)
    {
        if (!AnimationsEnabled) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var motion = Resources.GetValue(visual.Compositor, static c => new MotionResources(c));
        visual.StartAnimation("Opacity", motion.FadeIn);
        if (cue == MotionCue.Fade) return;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        visual.StartAnimation("Translation", motion.Move(cue));
    }

    internal static void SetVisible(FrameworkElement element, bool visible, MotionCue cue = MotionCue.Inline)
    {
        bool interrupted = CancelExit(element);
        var target = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!interrupted && element.Visibility == target) return;
        element.Visibility = target;
        if (visible) Reveal(element, cue);
    }

    // Only explicit dismissal of a floating panel gets an exit. No implicit
    // hide is installed on the tree: switching tabs or locking removes content
    // immediately, including when a dismissal was already in flight.
    internal static void SetOverlayVisible(FrameworkElement element, bool visible, MotionCue cue)
    {
        if (visible || !AnimationsEnabled || !element.IsLoaded)
        {
            SetVisible(element, visible, cue);
            return;
        }
        if (element.Visibility == Visibility.Collapsed || PendingExits.ContainsKey(element)) return;

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var motion = Resources.GetValue(visual.Compositor, static c => new MotionResources(c));
        var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var exit = new OverlayExit(batch, element.IsHitTestVisible);
        PendingExits.Add(element, exit);
        element.Unloaded += OverlayUnloaded;
        element.IsHitTestVisible = false;
        batch.Completed += (_, _) =>
        {
            if (PendingExits.TryGetValue(element, out var current) && ReferenceEquals(current, exit))
            {
                element.Visibility = Visibility.Collapsed;
                CancelExit(element);
            }
            batch.Dispose();
        };
        visual.StartAnimation("Opacity", motion.FadeOut);
        batch.End();
    }

    private static bool CancelExit(FrameworkElement element)
    {
        if (!PendingExits.Remove(element, out var exit)) return false;
        element.Unloaded -= OverlayUnloaded;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
        element.IsHitTestVisible = exit.WasHitTestVisible;
        return true;
    }

    private static void OverlayUnloaded(object sender, RoutedEventArgs args) =>
        SetVisible((FrameworkElement)sender, false);

    internal static void Open(InfoBar bar)
    {
        if (bar.IsOpen) return;
        bar.IsOpen = true;
        Reveal(bar);
    }
}
