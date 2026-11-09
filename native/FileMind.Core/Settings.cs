using System.Text.Json;

namespace FileMind.Core;

/// <summary>Engine: "auto" lets the app choose from the hardware; "cpu" and "nvidia" are an explicit choice.</summary>
public sealed class AppSettings
{
    public string Engine { get; set; } = "auto";
    /// <summary>"masonry", "rows", "contact" or "story".</summary>
    public string Layout { get; set; } = "masonry";
    public double TileSize { get; set; } = 260;
    public bool ShowNames { get; set; }
    /// <summary>Where the window was left (pixels), so it opens in the same place next time.</summary>
    public int WinX { get; set; } = int.MinValue;
    public int WinY { get; set; }
    public int WinW { get; set; }
    public int WinH { get; set; }
    public bool WinMax { get; set; }
    public double Transparency { get; set; } = 0.6;   // 0 solid … 1 most glass
    /// <summary>Colour theme key (see ThemeEngine) and the accent colour as #RRGGBB.</summary>
    public string Theme { get; set; } = "ink";
    public string Accent { get; set; } = "#FF5A1F";
    public double TileGap { get; set; } = 12;
    /// <summary>Volume the viewer starts at (0 to 1).</summary>
    public double Volume { get; set; } = 0.8;
    /// <summary>System-wide shortcut that opens the quick-search launcher.</summary>
    public string QuickSearchHotkey { get; set; } = "Ctrl+Shift+Space";
    /// <summary>Closing the window keeps File Mind in the notification area so the shortcut keeps working.</summary>
    public bool KeepRunning { get; set; } = true;

    public static AppSettings Load(AppPaths paths)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.SettingsFile)) ?? new(); }
        catch { return new(); }
    }

    public void Save(AppPaths paths) =>
        File.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
