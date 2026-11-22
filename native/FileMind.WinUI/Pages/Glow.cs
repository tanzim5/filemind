using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace FileMind.Pages;

/// <summary>A soft light that follows the pointer over a tile or card, drawn on the composition thread.
/// Only the element under the pointer has one, so it costs the same with ten tiles or ten thousand.</summary>
internal static class Glow
{
    private static bool Enabled => Anim.On && AppRuntime.Current.Settings.PointerLight;

    /// <summary>Lights <paramref name="e"/> while the pointer is over it, and lifts it slightly.</summary>
    public static void On(FrameworkElement e, double radius = 8, bool lift = true)
    {
        if (!Enabled || e.ActualWidth < 8 || e.ActualHeight < 8) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(e);
            var c = visual.Compositor;
            var brush = c.CreateRadialGradientBrush();
            var size = (float)Math.Max(e.ActualWidth, e.ActualHeight) * 0.8f;
            brush.EllipseRadius = new Vector2(size, size);
            brush.EllipseCenter = new Vector2((float)e.ActualWidth / 2, (float)e.ActualHeight / 3);
            brush.ColorStops.Add(c.CreateColorGradientStop(0f, Windows.UI.Color.FromArgb(46, 255, 255, 255)));
            brush.ColorStops.Add(c.CreateColorGradientStop(0.45f, Windows.UI.Color.FromArgb(14, 255, 255, 255)));
            brush.ColorStops.Add(c.CreateColorGradientStop(1f, Windows.UI.Color.FromArgb(0, 255, 255, 255)));

            // The light's centre follows the pointer, computed on the composition thread (no per-move work in the app).
            var pointer = ElementCompositionPreview.GetPointerPositionPropertySet(e);
            var follow = c.CreateExpressionAnimation("Vector2(pointer.Position.X, pointer.Position.Y)");
            follow.SetReferenceParameter("pointer", pointer);
            brush.StartAnimation("EllipseCenter", follow);

            var sprite = c.CreateSpriteVisual();
            sprite.Brush = brush;
            sprite.Size = new Vector2((float)e.ActualWidth, (float)e.ActualHeight);
            var r = (float)radius;
            sprite.Clip = c.CreateRectangleClip(0, 0, (float)e.ActualWidth, (float)e.ActualHeight, new Vector2(r), new Vector2(r), new Vector2(r), new Vector2(r));
            sprite.Opacity = 0;
            ElementCompositionPreview.SetElementChildVisual(e, sprite);

            var fade = c.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1, 1f);
            fade.Duration = TimeSpan.FromMilliseconds(140);
            sprite.StartAnimation("Opacity", fade);
            if (lift) Anim.Scale(e, 1.012f, 140);
        }
        catch (Exception) { }
    }

    public static void Off(FrameworkElement e, bool lift = true)
    {
        try
        {
            ElementCompositionPreview.SetElementChildVisual(e, null);
            if (lift) Anim.Scale(e, 1f, 160);
        }
        catch (Exception) { }
    }
}
