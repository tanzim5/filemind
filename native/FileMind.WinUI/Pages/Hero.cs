using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace FileMind.Pages;

/// <summary>The look of the opening screen and the first-run screen: a deep soft gradient taken from the user's accent colour and theme,
/// and the File Mind mark with a slow breathing glow.</summary>
internal static class Hero
{
    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(255, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static RadialGradientBrush Glow(Color c, byte alpha, double cx, double cy, double r) => new()
    {
        Center = new Windows.Foundation.Point(cx, cy), RadiusX = r, RadiusY = r, MappingMode = BrushMappingMode.RelativeToBoundingBox,
        GradientOrigin = new Windows.Foundation.Point(cx, cy),
        GradientStops =
        {
            new GradientStop { Color = Color.FromArgb(alpha, c.R, c.G, c.B), Offset = 0 },
            new GradientStop { Color = Color.FromArgb(0, c.R, c.G, c.B), Offset = 1 },
        },
    };

    /// <summary>Full-size layers: a deep base, a large accent glow high up and a second softer one low on the other side.
    /// With <paramref name="opaque"/> false only the glows are drawn, so it can sit over a page.</summary>
    public static UIElement Backdrop(bool opaque)
    {
        var st = AppRuntime.Current.Settings;
        var p = ThemeEngine.Find(st.Theme);
        var accent = ThemeEngine.AccentColor(st);
        var baseC = p.Dark ? Mix(p.Panel, Colors.Black, 0.35) : Mix(p.Panel, Colors.White, 0.4);
        var other = Mix(accent, p.Dark ? Color.FromArgb(255, 70, 60, 200) : Color.FromArgb(255, 255, 190, 140), 0.55);
        var g = new Grid { IsHitTestVisible = false };
        if (opaque) g.Background = new SolidColorBrush(baseC);
        g.Children.Add(new Rectangle { Fill = Glow(accent, (byte)(p.Dark ? 0x70 : 0x55), 0.5, 0.18, 0.75) });
        g.Children.Add(new Rectangle { Fill = Glow(other, (byte)(p.Dark ? 0x55 : 0x40), 0.9, 1.0, 0.8) });
        g.Children.Add(new Rectangle { Fill = Glow(accent, (byte)(p.Dark ? 0x30 : 0x28), 0.05, 0.85, 0.6) });
        return g;
    }

    /// <summary>The mark on a soft glow, breathing slowly (still when Windows has animations off).</summary>
    public static FrameworkElement Mark(double size)
    {
        var accent = ThemeEngine.AccentColor(AppRuntime.Current.Settings);
        var g = new Grid { Width = size * 2, Height = size * 2, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
        var glow = new Ellipse { Fill = Glow(accent, 0xA0, 0.5, 0.5, 0.5) };
        var mark = new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/icon.png")), Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(glow); g.Children.Add(mark);
        g.Loaded += (_, _) =>
        {
            if (!Anim.On) return;
            try
            {
                var gv = ElementCompositionPreview.GetElementVisual(glow); var mv = ElementCompositionPreview.GetElementVisual(mark);
                var c = gv.Compositor;
                gv.CenterPoint = new Vector3((float)size, (float)size, 0);
                mv.CenterPoint = new Vector3((float)size / 2, (float)size / 2, 0);
                var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.55f, 1f));
                var breathe = c.CreateVector3KeyFrameAnimation();
                breathe.InsertKeyFrame(0, new Vector3(1, 1, 1)); breathe.InsertKeyFrame(0.5f, new Vector3(1.045f, 1.045f, 1), ease); breathe.InsertKeyFrame(1, new Vector3(1, 1, 1), ease);
                breathe.Duration = TimeSpan.FromMilliseconds(3200); breathe.IterationBehavior = AnimationIterationBehavior.Forever;
                mv.StartAnimation("Scale", breathe);
                var pulse = c.CreateScalarKeyFrameAnimation();
                pulse.InsertKeyFrame(0, 0.55f); pulse.InsertKeyFrame(0.5f, 1f, ease); pulse.InsertKeyFrame(1, 0.55f, ease);
                pulse.Duration = TimeSpan.FromMilliseconds(3200); pulse.IterationBehavior = AnimationIterationBehavior.Forever;
                gv.StartAnimation("Opacity", pulse);
                var grow = c.CreateVector3KeyFrameAnimation();
                grow.InsertKeyFrame(0, new Vector3(0.92f, 0.92f, 1)); grow.InsertKeyFrame(0.5f, new Vector3(1.08f, 1.08f, 1), ease); grow.InsertKeyFrame(1, new Vector3(0.92f, 0.92f, 1), ease);
                grow.Duration = TimeSpan.FromMilliseconds(3200); grow.IterationBehavior = AnimationIterationBehavior.Forever;
                gv.StartAnimation("Scale", grow);
            }
            catch (Exception) { }
        };
        return g;
    }

    /// <summary>The one round primary action: a circle in the accent colour with its label inside.</summary>
    public static Button RoundButton(string text, double size = 120)
    {
        var accent = ThemeEngine.AccentColor(AppRuntime.Current.Settings);
        var fill = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0.2, 0), EndPoint = new Windows.Foundation.Point(0.8, 1) };
        fill.GradientStops.Add(new GradientStop { Color = Mix(accent, Colors.White, 0.18), Offset = 0 });
        fill.GradientStops.Add(new GradientStop { Color = Mix(accent, Colors.Black, 0.18), Offset = 1 });
        var b = new Button
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Padding = new Thickness(0), Background = fill,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)), BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Center, Shadow = new ThemeShadow(), Translation = new Vector3(0, 0, 28),
            Content = new TextBlock { Text = text, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = size - 28 },
        };
        b.Resources["ButtonBackgroundPointerOver"] = fill; b.Resources["ButtonBackgroundPressed"] = fill;
        b.Resources["ButtonBorderBrushPointerOver"] = b.BorderBrush; b.Resources["ButtonBorderBrushPressed"] = b.BorderBrush;
        b.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Colors.White); b.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Colors.White);
        return b;
    }
}
