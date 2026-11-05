using FileMind.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FileMind;

/// <summary>A colour theme: the dark or light base and the tint of the rail and page glass.</summary>
public sealed record Palette(string Key, string Name, bool Dark, Color Rail, Color Panel);

/// <summary>Applies the chosen theme and accent by changing the shared brushes in place, so every control follows at once.</summary>
public static class ThemeEngine
{
    private static Color C(string hex) => Windows.UI.Color.FromArgb(255, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));

    // Four looks, each tuned rather than many variations: a deep blue-black, a neutral graphite, true black for OLED
    // screens, and a warm light theme. Older theme keys fall back to the first one.
    public static readonly Palette[] All =
    [
        new("ink", "Midnight", true, C("#10111A"), C("#0C0D14")),
        new("graphite", "Graphite", true, C("#1E2024"), C("#17181B")),
        new("amoled", "Black", true, C("#000000"), C("#000000")),
        new("paper", "Light", false, C("#ECEAE6"), C("#F7F6F3")),
    ];

    public static readonly string[] Accents = ["#FF5A1F", "#F43F5E", "#F59E0B", "#22A559", "#3B82F6", "#8B5CF6"];

    public static ElementTheme Element() => Find(AppRuntime.Current.Settings.Theme).Dark ? ElementTheme.Dark : ElementTheme.Light;

    public static Palette Find(string key) => All.FirstOrDefault(p => p.Key == key) ?? All[0];

    private static void Set(string key, Color c)
    {
        if (Application.Current.Resources.TryGetValue(key, out var v) && v is SolidColorBrush b) b.Color = c;
    }

    private static void Fade(string key, Color c)
    {
        if (Application.Current.Resources.TryGetValue(key, out var v) && v is LinearGradientBrush g && g.GradientStops.Count == 2)
        {
            g.GradientStops[0].Color = c;
            g.GradientStops[1].Color = Windows.UI.Color.FromArgb(0, c.R, c.G, c.B);
        }
    }

    private static Color Lift(Color c, int d) => Windows.UI.Color.FromArgb(255, (byte)Math.Clamp(c.R + d, 0, 255), (byte)Math.Clamp(c.G + d, 0, 255), (byte)Math.Clamp(c.B + d, 0, 255));

    // One transparency setting (0 = solid, 1 = most glass). The floors keep text readable over any wallpaper:
    // the page never drops below 78 % opacity, the sidebar below 40 %.
    public static double RailOpacity(AppSettings st) => 1 - 0.60 * Math.Clamp(st.Transparency, 0, 1);
    public static double PanelOpacity(AppSettings st) => 1 - 0.22 * Math.Clamp(st.Transparency, 0, 1);

    public static void Apply(AppSettings st, FrameworkElement root)
    {
        var p = Find(st.Theme);
        root.RequestedTheme = p.Dark ? ElementTheme.Dark : ElementTheme.Light;
        byte W = p.Dark ? (byte)255 : (byte)0;           // glass veils are white on dark, black on light
        Color A(byte a) => Windows.UI.Color.FromArgb(a, W, W, W);
        Set("GlassFillBrush", A(0x1A)); Set("GlassFillStrongBrush", A(0x2A)); Set("GlassFillHoverBrush", A(0x24));
        Set("GlassHighlightBrush", A(0x26)); Set("GlassHoverSoftBrush", A(0x14));
        Set("GlassTextSecondaryBrush", p.Dark ? Windows.UI.Color.FromArgb(0xD1, 255, 255, 255) : Windows.UI.Color.FromArgb(0xD6, 20, 20, 24));
        Set("GlassTextTertiaryBrush", p.Dark ? Windows.UI.Color.FromArgb(0xA3, 255, 255, 255) : Windows.UI.Color.FromArgb(0xA8, 20, 20, 24));

        if (Application.Current.Resources.TryGetValue("GlassStrokeBrush", out var sv) && sv is LinearGradientBrush sg && sg.GradientStops.Count == 3)
        {
            sg.GradientStops[0].Color = A((byte)(p.Dark ? 0x40 : 0x30));
            sg.GradientStops[1].Color = A((byte)(p.Dark ? 0x10 : 0x14));
            sg.GradientStops[2].Color = A((byte)(p.Dark ? 0x22 : 0x20));
        }
        Set("DividerBrush", A((byte)(p.Dark ? 0x1F : 0x24)));
        Set("ScrimBrush", Windows.UI.Color.FromArgb(p.Dark ? (byte)0x66 : (byte)0x40, 0, 0, 0));
        var surf = Windows.UI.Color.FromArgb(255, p.Panel.R, p.Panel.G, p.Panel.B);
        Set("SurfaceBrush", p.Dark ? Lift(surf, 10) : Lift(surf, -6));
        Set("OverlayBrush", Windows.UI.Color.FromArgb(0xF4, surf.R, surf.G, surf.B));

        var ac = C(st.Accent.Length == 7 ? st.Accent : "#FF5A1F");
        Set("AccentFillBrush", Windows.UI.Color.FromArgb(0xCC, ac.R, ac.G, ac.B));
        Set("AccentTintBrush", Windows.UI.Color.FromArgb(0x4D, ac.R, ac.G, ac.B));
        Set("AccentOnFillBrush", Windows.UI.Color.FromArgb(0x66, ac.R, ac.G, ac.B));
        Set("AccentOnStrokeBrush", Windows.UI.Color.FromArgb(0x99, ac.R, ac.G, ac.B));
        Set("AccentBrush", ac);
        if (Application.Current.Resources.TryGetValue("AuraBrush", out var av) && av is RadialGradientBrush aura && aura.GradientStops.Count == 2)
        {
            aura.GradientStops[0].Color = Windows.UI.Color.FromArgb(p.Key == "amoled" ? (byte)0x14 : p.Dark ? (byte)0x26 : (byte)0x1C, ac.R, ac.G, ac.B);
            aura.GradientStops[1].Color = Windows.UI.Color.FromArgb(0, ac.R, ac.G, ac.B);
        }

        var fade = Windows.UI.Color.FromArgb((byte)(PanelOpacity(st) * 255), p.Panel.R, p.Panel.G, p.Panel.B);
        Fade("FadeTopBrush", fade); Fade("FadeBottomBrush", fade);
    }
}
