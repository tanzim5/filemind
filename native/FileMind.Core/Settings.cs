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
    /// <summary>How see-through the sidebar glass is (0 clear, 1 solid) and how solid the page panel is.</summary>
    public double RailGlass { get; set; } = 0.35;
    public double PanelGlass { get; set; } = 0.9;
    /// <summary>Colour theme key (see ThemeEngine) and the accent colour as #RRGGBB.</summary>
    public string Theme { get; set; } = "ink";
    public string Accent { get; set; } = "#FF5A1F";
    public double TileGap { get; set; } = 12;
    /// <summary>Sidebar destinations in the order shown, and those hidden. Sections of the filter rail likewise.</summary>
    public List<string> MenuOrder { get; set; } = ["library", "discover", "spaces", "duplicates"];
    public List<string> MenuHidden { get; set; } = ["duplicates"];
    public List<string> SectionOrder { get; set; } = ["types", "sources", "colours", "folders", "moods", "spaces", "details"];
    /// <summary>Volume the viewer starts at (0 to 1).</summary>
    public double Volume { get; set; } = 0.8;
    public List<string> SectionHidden { get; set; } = [];

    public static AppSettings Load(AppPaths paths)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.SettingsFile)) ?? new(); }
        catch { return new(); }
    }

    public void Save(AppPaths paths) =>
        File.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
