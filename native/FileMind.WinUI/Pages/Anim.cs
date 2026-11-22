using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FileMind.Pages;

/// <summary>The few small motions the app uses, all on the composition thread (cheap) and all off when Windows has
/// "Animation effects" turned off, in which case things simply appear.</summary>
internal static class Anim
{
    private static readonly Windows.UI.ViewManagement.UISettings Ui = new();

    public static bool On
    {
        get { try { return Ui.AnimationsEnabled; } catch { return true; } }
    }

    private static CubicBezierEasingFunction Ease(Compositor c) => c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));

    /// <summary>Fades an element in (and slides it up from <paramref name="fromY"/> pixels below) once.</summary>
    public static void In(UIElement? e, int ms = 150, double fromY = 0, int delayMs = 0)
    {
        if (e is null || !On) return;
        try
        {
            var v = ElementCompositionPreview.GetElementVisual(e);
            var c = v.Compositor;
            var fade = c.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0); fade.InsertKeyFrame(1, 1, Ease(c));
            fade.Duration = TimeSpan.FromMilliseconds(ms);
            if (delayMs > 0) { fade.DelayTime = TimeSpan.FromMilliseconds(delayMs); fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay; }
            v.StartAnimation("Opacity", fade);
            if (fromY != 0)
            {
                ElementCompositionPreview.SetIsTranslationEnabled(e, true);
                var slide = c.CreateScalarKeyFrameAnimation();
                slide.InsertKeyFrame(0, (float)fromY); slide.InsertKeyFrame(1, 0, Ease(c));
                slide.Duration = TimeSpan.FromMilliseconds(ms + 40);
                if (delayMs > 0) { slide.DelayTime = TimeSpan.FromMilliseconds(delayMs); slide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay; }
                v.StartAnimation("Translation.Y", slide);
            }
        }
        catch (Exception) { }
    }

    /// <summary>Fades an element out, then runs <paramref name="done"/> (at once when animations are off).</summary>
    public static void Out(UIElement e, int ms, Action done)
    {
        if (!On) { done(); return; }
        try
        {
            var v = ElementCompositionPreview.GetElementVisual(e);
            var c = v.Compositor;
            var a = c.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(1, 0, Ease(c)); a.Duration = TimeSpan.FromMilliseconds(ms);
            var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
            v.StartAnimation("Opacity", a);
            batch.End();
            batch.Completed += (_, _) => e.DispatcherQueue.TryEnqueue(() => { done(); v.Opacity = 1; });
        }
        catch (Exception) { done(); }
    }

    internal static void Scale(UIElement e, float to, int ms)
    {
        try
        {
            var v = ElementCompositionPreview.GetElementVisual(e);
            if (e is FrameworkElement fe) v.CenterPoint = new Vector3((float)fe.ActualWidth / 2, (float)fe.ActualHeight / 2, 0);
            var a = v.Compositor.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(1, new Vector3(to, to, 1), Ease(v.Compositor));
            a.Duration = TimeSpan.FromMilliseconds(ms);
            v.StartAnimation("Scale", a);
        }
        catch (Exception) { }
    }

    private static ButtonBase? _down;

    /// <summary>Every button and chip under <paramref name="root"/> dips to 97% while pressed.</summary>
    public static void HookPress(UIElement root)
    {
        root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!On) return;
            for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
                if (d is ButtonBase b && b.IsEnabled) { _down = b; Scale(b, 0.97f, 70); return; }
        }), true);
        void Up(object s, PointerRoutedEventArgs e) { if (_down is { } b) { _down = null; Scale(b, 1f, 120); } }
        root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Up), true);
        root.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Up), true);
        root.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(Up), true);
    }
}
