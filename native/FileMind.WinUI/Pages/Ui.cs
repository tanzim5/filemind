using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FileMind.Pages;

/// <summary>Resource lookups that cannot throw. A missing key gives null (and a log line) instead of a crash.</summary>
internal static class Ui
{
    public static T? Get<T>(string key) where T : class
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var v) && v is T t) return t;
        }
        catch (Exception ex) { App.Log("resource", ex, key); }
        App.Log("resource", null, $"missing {typeof(T).Name} '{key}'");
        return null;
    }

    public static Style? Style(string key) => Get<Style>(key);
    public static Brush? Brush(string key) => Get<Brush>(key);

    public static SolidColorBrush Hex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        if (hex.Length != 8 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out _))
            return new SolidColorBrush(Microsoft.UI.Colors.Gray);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16), Convert.ToByte(hex[6..8], 16)));
    }

    /// <summary>Applies the surface style to a floating pill: fully rounded and with an inner top highlight in liquid glass,
    /// the usual radius 8 in frosted glass. The first call wraps the pill's content so the highlight can sit behind it.</summary>
    public static void Surface(Microsoft.UI.Xaml.Controls.Border b, double pillRadius)
    {
        var liquid = FileMind.ThemeEngine.Liquid;
        b.CornerRadius = new CornerRadius(liquid ? pillRadius : 8);
        if (b.Child is not Microsoft.UI.Xaml.Controls.Grid { Tag: "sheen" } g)
        {
            var child = b.Child; b.Child = null;
            g = new Microsoft.UI.Xaml.Controls.Grid { Tag = "sheen" };
            g.Children.Add(new Microsoft.UI.Xaml.Controls.Border { IsHitTestVisible = false, Margin = new Thickness(1), Background = Brush("LiquidSheenBrush") });
            g.Children.Add(new Microsoft.UI.Xaml.Controls.Border { IsHitTestVisible = false, Margin = new Thickness(-1), BorderThickness = new Thickness(1), BorderBrush = Brush("LiquidEdgeBrush") });
            if (child is not null) g.Children.Add(child);
            b.Child = g;
        }
        ((Microsoft.UI.Xaml.Controls.Border)g.Children[0]).CornerRadius = new CornerRadius(Math.Max(0, b.CornerRadius.TopLeft - 1));
        ((Microsoft.UI.Xaml.Controls.Border)g.Children[1]).CornerRadius = b.CornerRadius;
        if (b.Shadow is ThemeShadow) b.Translation = new System.Numerics.Vector3(0, 0, liquid ? 32 : 24);
    }
}
