using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace FileMind.Pages;

/// <summary>A thin, round-capped outline icon on a 24-unit grid, drawn in the style of the ChatGPT app's icons
/// (a stroke of about 1.6 with rounded ends and joins). It takes its colour from Foreground.</summary>
public sealed class LineIcon : UserControl
{
    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Circle(double cx, double cy, double r) => $"M{N(cx - r)},{N(cy)} a{N(r)},{N(r)} 0 1 0 {N(2 * r)},0 a{N(r)},{N(r)} 0 1 0 {N(-2 * r)},0 Z";
    private static string Box(double x, double y, double w, double h, double r) =>
        $"M{N(x + r)},{N(y)} H{N(x + w - r)} A{N(r)},{N(r)} 0 0 1 {N(x + w)},{N(y + r)} V{N(y + h - r)} A{N(r)},{N(r)} 0 0 1 {N(x + w - r)},{N(y + h)} H{N(x + r)} A{N(r)},{N(r)} 0 0 1 {N(x)},{N(y + h - r)} V{N(y + r)} A{N(r)},{N(r)} 0 0 1 {N(x + r)},{N(y)} Z";

    private static readonly Dictionary<string, string> Paths = new()
    {
        ["back"] = "M15,18 L9,12 L15,6",
        ["fwd"] = "M9,18 L15,12 L9,6",
        ["filter"] = "M21,4 H14 M10,4 H3 M21,12 H12 M8,12 H3 M21,20 H16 M12,20 H3 M14,2 V6 M8,10 V14 M16,18 V22",
        ["search"] = Circle(11, 11, 8) + " M21,21 L16.65,16.65",
        ["photo"] = Box(3, 3, 18, 18, 2.5) + " " + Circle(9, 9, 2) + " M21,15 L17.9,11.9 A2,2 0 0 0 15.1,11.9 L6,21",
        ["mic"] = "M12,2 A3,3 0 0 0 9,5 V11 A3,3 0 0 0 15,11 V5 A3,3 0 0 0 12,2 Z M19,10 V11 A7,7 0 0 1 5,11 V10 M12,18 V22",
        ["sort"] = "M21,16 L17,20 L13,16 M17,20 V4 M3,8 L7,4 L11,8 M7,4 V20",
        ["grid"] = Box(3, 3, 7, 7, 1.5) + " " + Box(14, 3, 7, 7, 1.5) + " " + Box(14, 14, 7, 7, 1.5) + " " + Box(3, 14, 7, 7, 1.5),
        ["all"] = Box(3, 3, 7, 7, 1.5) + " " + Box(14, 3, 7, 7, 1.5) + " " + Box(14, 14, 7, 7, 1.5) + " " + Box(3, 14, 7, 7, 1.5),
        ["rows"] = "M8,6 H21 M8,12 H21 M8,18 H21 M3,6 H3.01 M3,12 H3.01 M3,18 H3.01",
        ["story"] = Box(3, 3, 18, 18, 2.5) + " M21,9 H3 M21,15 H3",
        ["minus"] = "M5,12 H19",
        ["plus"] = "M5,12 H19 M12,5 V19",
        ["folderplus"] = "M12,10 V16 M9,13 H15 M20,20 A2,2 0 0 0 22,18 V8 A2,2 0 0 0 20,6 H12.1 A2,2 0 0 1 10.41,5.1 L9.6,3.9 A2,2 0 0 0 7.93,3 H4 A2,2 0 0 0 2,5 V18 A2,2 0 0 0 4,20 Z",
        ["library"] = Box(3, 7, 14, 14, 3) + " M7,3 H18 A3,3 0 0 1 21,6 V17 " + Circle(8, 12, 1.4) + " M17,17 L13.2,13.2 A1.4,1.4 0 0 0 11.2,13.2 L4,20.5",
        ["discover"] = "M11,5 C11.6,10 13,11.4 18,12 C13,12.6 11.6,14 11,19 C10.4,14 9,12.6 4,12 C9,11.4 10.4,10 11,5 Z M19,2.5 V7.5 M16.5,5 H21.5",
        ["spaces"] = "M3,7 A2,2 0 0 1 5,5 H9.4 A2,2 0 0 1 11,5.8 L11.9,7 H19 A2,2 0 0 1 21,9 V17 A2,2 0 0 1 19,19 H5 A2,2 0 0 1 3,17 Z",
        ["duplicates"] = Box(8, 8, 14, 14, 2) + " M4,16 C2.9,16 2,15.1 2,14 V4 C2,2.9 2.9,2 4,2 H14 C15.1,2 16,2.9 16,4",
        ["settings"] = "M12.22,2 H11.78 A2,2 0 0 0 9.78,4 V4.18 A2,2 0 0 1 8.78,5.91 L8.35,6.16 A2,2 0 0 1 6.35,6.16 L6.2,6.08 A2,2 0 0 0 3.47,6.81 L3.25,7.19 A2,2 0 0 0 3.98,9.92 L4.13,10.02 A2,2 0 0 1 5.13,11.74 V12.25 A2,2 0 0 1 4.13,13.99 L3.98,14.08 A2,2 0 0 0 3.25,16.81 L3.47,17.19 A2,2 0 0 0 6.2,17.92 L6.35,17.84 A2,2 0 0 1 8.35,17.84 L8.78,18.09 A2,2 0 0 1 9.78,19.82 V20 A2,2 0 0 0 11.78,22 H12.22 A2,2 0 0 0 14.22,20 V19.82 A2,2 0 0 1 15.22,18.09 L15.65,17.84 A2,2 0 0 1 17.65,17.84 L17.8,17.92 A2,2 0 0 0 20.53,17.53 L20.75,17.14 A2,2 0 0 0 20.02,14.41 L19.87,14.33 A2,2 0 0 1 18.87,12.59 V12.09 A2,2 0 0 1 19.87,10.35 L20.02,10.26 A2,2 0 0 0 20.75,7.53 L20.53,7.15 A2,2 0 0 0 17.8,6.42 L17.65,6.5 A2,2 0 0 1 15.65,6.5 L15.22,6.25 A2,2 0 0 1 14.22,4.52 V4 A2,2 0 0 0 12.22,2 Z " + Circle(12, 12, 3),
        ["folder"] = "M3,7 A2,2 0 0 1 5,5 H9.4 A2,2 0 0 1 11,5.8 L11.9,7 H19 A2,2 0 0 1 21,9 V17 A2,2 0 0 1 19,19 H5 A2,2 0 0 1 3,17 Z",
        ["trash"] = "M3,6 H21 M8,6 V4 A2,2 0 0 1 10,2 H14 A2,2 0 0 1 16,4 V6 M19,6 L18.1,19.2 A2,2 0 0 1 16.1,21 H7.9 A2,2 0 0 1 5.9,19.2 L5,6 M10,11 V16 M14,11 V16",
        ["bolt"] = "M13,2 L4,14 H12 L11,22 L20,10 H12 Z",
        ["keyboard"] = Box(2, 5, 20, 14, 2) + " M6,9 H6.01 M10,9 H10.01 M14,9 H14.01 M18,9 H18.01 M6,13 H6.01 M18,13 H18.01 M10,13 H14 M7,16 H17",
        ["chip"] = Box(5, 5, 14, 14, 2) + " " + Box(9, 9, 6, 6, 1) + " M9,2 V5 M15,2 V5 M9,19 V22 M15,19 V22 M2,9 H5 M2,15 H5 M19,9 H22 M19,15 H22",
        ["info"] = Circle(12, 12, 9.5) + " M12,16 V11.5 M12,8 H12.01",
        ["eye"] = "M2,12 C4.5,7 8,5 12,5 C16,5 19.5,7 22,12 C19.5,17 16,19 12,19 C8,19 4.5,17 2,12 Z " + Circle(12, 12, 3),
        ["power"] = "M12,3 V12 M18.4,6.6 A9,9 0 1 1 5.6,6.6",
        ["sliders"] = "M4,21 V14 M4,10 V3 M12,21 V12 M12,8 V3 M20,21 V16 M20,12 V3 M1,14 H7 M9,8 H15 M17,16 H23",
        ["sound"] = "M11,5 L6,9 H3 V15 H6 L11,19 Z M15.5,8.5 A5,5 0 0 1 15.5,15.5 M18.5,5.5 A9,9 0 0 1 18.5,18.5",
        ["scan"] = "M3,7 V5 A2,2 0 0 1 5,3 H7 M17,3 H19 A2,2 0 0 1 21,5 V7 M21,17 V19 A2,2 0 0 1 19,21 H17 M7,21 H5 A2,2 0 0 1 3,19 V17 M7,12 H17",
        ["sparkle"] = "M12,3 C12.8,9 15,11.2 21,12 C15,12.8 12.8,15 12,21 C11.2,15 9,12.8 3,12 C9,11.2 11.2,9 12,3 Z",
        ["command"] = "M15,6 V18 A3,3 0 1 0 18,15 H6 A3,3 0 1 0 9,18 V6 A3,3 0 1 0 6,9 H18 A3,3 0 1 0 15,6 Z",
        ["close"] = "M18,6 L6,18 M6,6 L18,18",
        ["check"] = "M20,6 L9,17 L4,12",
        ["play"] = "M7,4.5 L19,12 L7,19.5 Z",
        ["pause"] = "M8,5 V19 M16,5 V19",
        ["palette"] = "M12,22 A1,1 0 0 1 12,2 A10,9 0 0 1 22,11 A5,5 0 0 1 17,16 H14.75 A1.75,1.75 0 0 0 13.35,18.8 L13.65,19.2 A1.75,1.75 0 0 1 12.25,22 Z M13.5,6.5 H13.51 M17.5,10.5 H17.51 M6.5,12.5 H6.51 M8.5,7.5 H8.51",
        ["text"] = "M4,7 V4 H20 V7 M9,20 H15 M12,4 V20",
        ["music"] = "M9,18 V5 L21,3 V16 " + Circle(6, 18, 3) + " " + Circle(18, 16, 3),
        ["audio"] = "M9,18 V5 L21,3 V16 " + Circle(6, 18, 3) + " " + Circle(18, 16, 3),
        ["video"] = Box(2, 6, 14, 12, 2.5) + " M16,13 L21.22,16.48 A0.5,0.5 0 0 0 22,16.07 V7.87 A0.5,0.5 0 0 0 21.25,7.44 L16,10.5",
        ["file"] = "M15,2 H6 A2,2 0 0 0 4,4 V20 A2,2 0 0 0 6,22 H18 A2,2 0 0 0 20,20 V7 Z M14,2 V6 A2,2 0 0 0 16,8 H20",
        ["refresh"] = "M3,12 A9,9 0 0 1 12,3 A9.75,9.75 0 0 1 18.74,5.74 L21,8 M21,3 V8 H16 M21,12 A9,9 0 0 1 12,21 A9.75,9.75 0 0 1 5.26,18.26 L3,16 M8,16 H3 V21",
    };

    private readonly Microsoft.UI.Xaml.Shapes.Path _path = new() { StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
    private readonly Viewbox _box = new() { Stretch = Stretch.Uniform };

    public LineIcon()
    {
        IsTabStop = false;
        var host = new Grid { Width = 24, Height = 24 };
        host.Children.Add(_path);
        _box.Child = host;
        Content = _box;
        _path.SetBinding(Shape.StrokeProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
        Size = 16;
        Loaded += (_, _) => FollowTheme();
        ActualThemeChanged += (_, _) => FollowTheme();
    }

    // Without an explicit colour the icon is ink on light themes and white on dark ones, so it is readable on every theme.
    private void FollowTheme()
    {
        if (!_auto && ReadLocalValue(ForegroundProperty) != DependencyProperty.UnsetValue) return;
        _auto = true;
        Foreground = new SolidColorBrush(ActualTheme == ElementTheme.Light ? Windows.UI.Color.FromArgb(0xE6, 24, 24, 28) : Windows.UI.Color.FromArgb(0xE6, 255, 255, 255));
    }
    private bool _auto;

    public string Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            // An unknown name or a malformed path leaves the icon blank; it never takes the app down.
            try { if (Paths.TryGetValue(value ?? "", out var d)) _path.Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), d); }
            catch (Exception ex) { App.Log("icon", ex, value); }
        }
    }
    private string _kind = "";

    // Stroke is the width on screen in pixels, so icons stay equally crisp at every size (the path is drawn on a
    // 24-unit grid and scaled; a fixed grid stroke would turn hairline-thin and grey at 12-14 px).
    private double _stroke = 1.5;
    public double Stroke { get => _stroke; set { _stroke = value; Restroke(); } }
    private void Restroke() => _path.StrokeThickness = _stroke * 24 / Math.Max(1, _box.Width);

    public double Size { get => _box.Width; set { _box.Width = value; _box.Height = value; Restroke(); } }
}
