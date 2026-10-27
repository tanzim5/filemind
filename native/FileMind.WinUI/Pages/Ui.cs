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
}
