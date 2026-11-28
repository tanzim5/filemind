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
    private static readonly HashSet<ContainerVisual> Leaving = [];

    private static Windows.UI.Color Accent()
    {
        try { var h = AppRuntime.Current.Settings.Accent.TrimStart('#'); return Windows.UI.Color.FromArgb(255, Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16)); }
        catch (Exception) { return Windows.UI.Color.FromArgb(255, 255, 90, 31); }
    }

    /// <summary>True while the pointer is still inside <paramref name="e"/> (moving between its inner parts is not leaving it).</summary>
    public static bool Inside(FrameworkElement e, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs a)
    {
        var p = a.GetCurrentPoint(e).Position;
        return p.X > 0.5 && p.Y > 0.5 && p.X < e.ActualWidth - 0.5 && p.Y < e.ActualHeight - 0.5;
    }

    /// <summary>Lights <paramref name="e"/> while the pointer is over it. <paramref name="radius"/> is its corner radius; <paramref name="size"/> how far the light reaches.</summary>
    public static void On(FrameworkElement e, double radius = 8, bool lift = true, float size = 140, bool rim = true)
    {
        if (!Enabled || e.ActualWidth < 8 || e.ActualHeight < 8) return;
        if (Live.TryGetValue(e, out var already) && !Leaving.Contains(already)) return;      // already lit
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(e);
            var c = visual.Compositor;
            var dark = ThemeEngine.Find(AppRuntime.Current.Settings.Theme).Dark;
            float w = (float)e.ActualWidth, h = (float)e.ActualHeight, r = (float)radius;

            var pointer = ElementCompositionPreview.GetPointerPositionPropertySet(e);
            var accent = Accent();
            CompositionRadialGradientBrush Radial(float reach, byte centre, byte mid, Windows.UI.Color tone)
            {
                var b = c.CreateRadialGradientBrush();
                b.MappingMode = CompositionMappingMode.Absolute;     // pixels, not 0..1: the light is centred where the pointer is
                b.EllipseRadius = new Vector2(reach, reach);
                b.EllipseCenter = new Vector2(w / 2, h / 3);
                b.ColorStops.Add(c.CreateColorGradientStop(0f, Windows.UI.Color.FromArgb(centre, tone.R, tone.G, tone.B)));
                b.ColorStops.Add(c.CreateColorGradientStop(0.5f, Windows.UI.Color.FromArgb(mid, tone.R, tone.G, tone.B)));
                b.ColorStops.Add(c.CreateColorGradientStop(1f, Windows.UI.Color.FromArgb(0, tone.R, tone.G, tone.B)));
                var follow = c.CreateExpressionAnimation("Vector2(p.Position.X, p.Position.Y)");
                follow.SetReferenceParameter("p", pointer);
                b.StartAnimation("EllipseCenter", follow);
                return b;
            }

            var host = c.CreateContainerVisual();
            host.Size = new Vector2(w, h);
            var fill = c.CreateSpriteVisual();
            fill.Size = host.Size;
            var white = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            fill.Brush = dark ? Radial(size, 0x1C, 0x08, Mix(white, accent, 0.45)) : Radial(size, 0x4C, 0x14, Mix(white, accent, 0.25));
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
                shape.StrokeBrush = dark ? Radial(size * 0.8f, 0xB0, 0x30, Mix(white, accent, 0.7)) : Radial(size * 0.8f, 0x90, 0x28, accent);
                var sv = c.CreateShapeVisual();
                sv.Size = host.Size;
                sv.Shapes.Add(shape);
                host.Children.InsertAtTop(sv);
            }

            host.Opacity = 0; Leaving.Remove(host);
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
            if (!Live.TryGetValue(e, out var host) || !Leaving.Add(host)) return;
            if (!Anim.On) { Leaving.Remove(host); ElementCompositionPreview.SetElementChildVisual(e, null); Live.Remove(e); return; }
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
                Leaving.Remove(host);
                if (Live.TryGetValue(e, out var now) && ReferenceEquals(now, host)) { ElementCompositionPreview.SetElementChildVisual(e, null); Live.Remove(e); }
            });
        }
        catch (Exception) { }
    }

    private static Windows.UI.Color Mix(Windows.UI.Color a, Windows.UI.Color b, double t) =>
        Windows.UI.Color.FromArgb(255, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>Gives <paramref name="e"/> the light for as long as it lives (mouse only; touch and pen get none).</summary>
    public static void Attach(FrameworkElement e, double radius = 8, float size = 140, bool rim = true, bool lift = false)
    {
        e.PointerEntered += (_, a) => { if (a.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse) On(e, radius, lift, size, rim); };
        e.PointerExited += (_, a) => { if (!Inside(e, a)) Off(e, lift); };
    }
}
