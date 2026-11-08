using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FileMind.Pages;

/// <summary>A small colour tile with a white line glyph, like the module icons in CleanMyMac or the categories in
/// Windows Settings. Each destination and settings group has its own two-tone gradient, so they are recognisable at a
/// glance. Corners stay modest (about a fifth of the size).</summary>
public sealed class AppIcon : UserControl
{
    private static readonly Dictionary<string, (string From, string To)> Tones = new()
    {
        ["library"] = ("#4F7BFF", "#7A4DFF"),
        ["discover"] = ("#FF4D8D", "#FF8A3D"),
        ["spaces"] = ("#14B8A6", "#22A559"),
        ["duplicates"] = ("#FFB020", "#F2552C"),
        ["search"] = ("#8B5CF6", "#4F7BFF"),
        ["voice"] = ("#EC4899", "#8B5CF6"),
        ["engine"] = ("#22C55E", "#0EA5A4"),
        ["appearance"] = ("#F59E0B", "#EC4899"),
        ["folders"] = ("#3B82F6", "#06B6D4"),
        ["about"] = ("#64748B", "#334155"),
        ["sound"] = ("#06B6D4", "#6366F1"),
        ["power"] = ("#6366F1", "#0EA5E9"),
    };

    private readonly Border _tile = new();
    private readonly LineIcon _glyph = new() { Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    public AppIcon()
    {
        IsTabStop = false;
        _tile.Child = _glyph;
        Content = _tile;
        Size = 28;
    }

    /// <summary>The colour family (see Tones); defaults to the glyph name.</summary>
    public string Tone { get => _tone; set { _tone = value; Paint(); } }
    private string _tone = "";

    public string Glyph { get => _glyph.Kind; set { _glyph.Kind = value; if (_tone.Length == 0) Paint(); } }

    public double Size
    {
        get => _tile.Width;
        set
        {
            _tile.Width = value; _tile.Height = value;
            _tile.CornerRadius = new CornerRadius(Math.Round(value * 0.22));
            _glyph.Size = Math.Round(value * 0.56); _glyph.Stroke = Math.Max(1.5, value * 0.06);
        }
    }

    private void Paint()
    {
        var key = _tone.Length > 0 ? _tone : _glyph.Kind;
        var (a, b) = Tones.TryGetValue(key, out var t) ? t : ("#64748B", "#334155");
        var g = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
        g.GradientStops.Add(new GradientStop { Color = Ui.Hex(a).Color, Offset = 0 });
        g.GradientStops.Add(new GradientStop { Color = Ui.Hex(b).Color, Offset = 1 });
        _tile.Background = g;
        // A faint top highlight gives the tile a little depth without looking glossy.
        _tile.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 255, 255, 255));
        _tile.BorderThickness = new Thickness(1, 1, 0, 0);
    }
}
