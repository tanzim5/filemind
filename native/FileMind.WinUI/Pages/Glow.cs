using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace FileMind.Pages;

/// <summary>A soft light that follows the pointer over a tile, card, button or panel, with a hairline edge that brightens near it.
/// Drawn on the composition thread. Only the element under the pointer has one, so it costs the same with ten tiles or ten thousand.</summary>
internal static class Glow
{
    private static bool Enabled => Anim.On && AppRuntime.Current.Settings.PointerLight;
    private static readonly ConditionalWeakTable<FrameworkElement, ContainerVisual> Live = new();

    /// <summary>Lights <paramref name="e"/> while the pointer is over it. <paramref name="radius"/> is its corner radius; <paramref name="size"/> how far the light reaches.</summary>
    public static void On(FrameworkElement e, double radius = 8, bool lift = true, float size = 140, bool rim = true)
    {
        if (!Enabled || e.ActualWidth < 8 || e.ActualHeight < 8) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(e);
            var c = visual.Compositor;
            var dark = ThemeEngine.Find(AppRuntime.Current.Settings.Theme).Dark;
            float w = (float)e.ActualWidth, h = (float)e.ActualHeight, r = (float)radius;

            var pointer = ElementCompositionPreview.GetPointerPositionPropertySet(e);
            CompositionRadialGradientBrush Radial(float reach, byte centre, byte mid, byte tone)
            {
                var b = c.CreateRadialGradientBrush();
                b.MappingMode = CompositionMappingMode.Absolute;     // pixels, not 0..1: the light is centred where the pointer is
                b.EllipseRadius = new Vector2(reach, reach);
                b.EllipseCenter = new Vector2(w / 2, h / 3);
                b.ColorStops.Add(c.CreateColorGradientStop(0f, Windows.UI.Color.FromArgb(centre, tone, tone, tone)));
                b.ColorStops.Add(c.CreateColorGradientStop(0.5f, Windows.UI.Color.FromArgb(mid, tone, tone, tone)));
                b.ColorStops.Add(c.CreateColorGradientStop(1f, Windows.UI.Color.FromArgb(0, tone, tone, tone)));
                var follow = c.CreateExpressionAnimation("Vector2(p.Position.X, p.Position.Y)");
                follow.SetReferenceParameter("p", pointer);
                b.StartAnimation("EllipseCenter", follow);
                return b;
            }

            var host = c.CreateContainerVisual();
            host.Size = new Vector2(w, h);
            var fill = c.CreateSpriteVisual();
            fill.Size = host.Size;
            fill.Brush = dark ? Radial(size, 0x1A, 0x08, 255) : Radial(size, 0x5C, 0x14, 255);
            fill.Clip = c.CreateRectangleClip(0, 0, w, h, new Vector2(r), new Vector2(r), new Vector2(r), new Vector2(r));
            host.Children.InsertAtTop(fill);

            if (rim)
            {
                var geo = c.CreateRoundedRectangleGeometry();
                geo.Offset = new Vector2(0.5f, 0.5f);
                geo.Size = new Vector2(Math.Max(1, w - 1), Math.Max(1, h - 1));
                geo.CornerRadius = new Vector2(Math.Max(0, r - 0.5f));
                var shape = c.CreateSpriteShape(geo);
                shape.StrokeThickness = 1;
                shape.StrokeBrush = dark ? Radial(size * 0.75f, 0x5C, 0x1E, 255) : Radial(size * 0.75f, 0x2E, 0x0A, 0);
                var sv = c.CreateShapeVisual();
                sv.Size = host.Size;
                sv.Shapes.Add(shape);
                host.Children.InsertAtTop(sv);
            }

            host.Opacity = 0;
            ElementCompositionPreview.SetElementChildVisual(e, host);
            Live.Remove(e); Live.Add(e, host);
            var fade = c.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1, 1f);
            fade.Duration = TimeSpan.FromMilliseconds(140);
            host.StartAnimation("Opacity", fade);
            if (lift) Anim.Spring(e, 1.01f, 0.8f, 50);
        }
        catch (Exception) { }
    }

    public static void Off(FrameworkElement e, bool lift = true)
    {
        try
        {
            if (lift) Anim.Spring(e, 1f, 0.8f, 50);
            if (!Live.TryGetValue(e, out var host)) return;
            if (!Anim.On) { ElementCompositionPreview.SetElementChildVisual(e, null); Live.Remove(e); return; }
            var c = host.Compositor;
            var fade = c.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1, 0f);
            fade.Duration = TimeSpan.FromMilliseconds(180);
            var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
            host.StartAnimation("Opacity", fade);
            batch.End();
            batch.Completed += (_, _) => e.DispatcherQueue.TryEnqueue(() =>
            {
                // Only clear it if the pointer has not come back and started a newer light in the meantime.
                if (Live.TryGetValue(e, out var now) && ReferenceEquals(now, host)) { ElementCompositionPreview.SetElementChildVisual(e, null); Live.Remove(e); }
            });
        }
        catch (Exception) { }
    }

    /// <summary>Gives <paramref name="e"/> the light for as long as it lives (mouse only; touch and pen get none).</summary>
    public static void Attach(FrameworkElement e, double radius = 8, float size = 140, bool rim = true, bool lift = false)
    {
        e.PointerEntered += (_, a) => { if (a.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse) On(e, radius, lift, size, rim); };
        e.PointerExited += (_, _) => Off(e, lift);
    }
}
