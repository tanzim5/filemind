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
    public string Theme { get; set; } = "paper";
    public string Accent { get; set; } = "#FF5A1F";
    /// <summary>Surface style: "frosted" (frosted glass, the default) or "liquid" (clearer, brighter glass with bright edges).</summary>
    public string Surface { get; set; } = "frosted";
    public double TileGap { get; set; } = 10;
    /// <summary>Volume the viewer starts at (0 to 1).</summary>
    public double Volume { get; set; } = 0.8;
    /// <summary>System-wide shortcut that opens the quick-search launcher.</summary>
    public string QuickSearchHotkey { get; set; } = "Ctrl+Shift+Space";
    /// <summary>Closing the window keeps File Mind in the notification area so the shortcut keeps working.</summary>
    public bool KeepRunning { get; set; } = true;
    /// <summary>Voice search: "local" (Whisper on this PC) or "windows" (Windows speech recognition).</summary>
    public string VoiceEngine { get; set; } = "local";
    /// <summary>How hard indexing works the PC: "quiet" (one helper, gentle on fans), "balanced" or "fast".</summary>
    public string IndexSpeed { get; set; } = "balanced";
    /// <summary>Sidebar sections the person folded away (by key, e.g. "spaces" or "refine:Shape").</summary>
    public List<string> Collapsed { get; set; } = [];
    /// <summary>Hide pictures and clips smaller than this many pixels (0 = show everything).</summary>
    public int MinSide { get; set; }
    /// <summary>Look for a newer version at start-up (at most once a day).</summary>
    public bool CheckUpdates { get; set; } = true;
    /// <summary>A soft light follows the pointer over tiles and cards.</summary>
    public bool PointerLight { get; set; } = true;
    /// <summary>"slim" (the detailed filters sit behind a Filters button) or "classic" (every section in the left sidebar).</summary>
    public string SidebarStyle { get; set; } = "slim";
    public string? SkipVersion { get; set; }
    public long LastUpdateCheck { get; set; }
    /// <summary>"short" or "long": which side of a picture the size is measured on.</summary>
    public string MinSideMode { get; set; } = "short";
    /// <summary>Order of the Library's sidebar sections (keys, top to bottom). Empty means the default order.</summary>
    public List<string> SidebarOrder { get; set; } = [];
    /// <summary>Where the viewer shows similar files: "below" the file (default) or "side" (the Similar tab in the side panel).</summary>
    public string SimilarFiles { get; set; } = "below";

    public static AppSettings Load(AppPaths paths)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.SettingsFile)) ?? new(); }
        catch { return new(); }
    }

    public void Save(AppPaths paths) =>
        File.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
