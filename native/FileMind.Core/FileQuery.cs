using System.Globalization;
using System.Text.RegularExpressions;

namespace FileMind.Core;

/// <summary>Every way the library can be narrowed, mirroring what the engine's /api/files and /api/search accept.</summary>
public sealed class FileQuery
{
    public string Kind { get; set; } = "all";
    public int? Root { get; set; }
    public int? Project { get; set; }
    public bool Unassigned { get; set; }
    public string? Color { get; set; }
    public string? Near { get; set; }
    public string? Source { get; set; }
    public string? Discover { get; set; }
    public string? Pattern { get; set; }
    public string? Tag { get; set; }
    public string Sort { get; set; } = "mtime";

    public double? After { get; set; }
    public double? Before { get; set; }
    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }
    public int? MinWidth { get; set; }
    public int? MinHeight { get; set; }
    public long? MinPixels { get; set; }
    public double? MinDuration { get; set; }
    public double? MaxDuration { get; set; }
    public double? MinFps { get; set; }
    public string? Aspect { get; set; }
    public string? Codec { get; set; }

    public FileQuery Clone() => (FileQuery)MemberwiseClone();

    public bool HasDetailFilters => DetailFilters().Count > 0;

    /// <summary>The detail filters under the engine's own names; shared by browsing and searching.</summary>
    public Dictionary<string, object> DetailFilters()
    {
        var d = new Dictionary<string, object>();
        void Add(string k, object? v) { if (v is not null && (v is not string s || s.Length > 0)) d[k] = v; }
        Add("after", After); Add("before", Before); Add("min_size", MinSize); Add("max_size", MaxSize);
        Add("min_width", MinWidth); Add("min_height", MinHeight); Add("min_pixels", MinPixels);
        Add("min_duration", MinDuration); Add("max_duration", MaxDuration); Add("min_fps", MinFps);
        Add("aspect", Aspect); Add("codec", Codec);
        return d;
    }

    public void ClearDetailFilters()
    {
        After = Before = null; MinSize = MaxSize = null; MinWidth = MinHeight = null; MinPixels = null;
        MinDuration = MaxDuration = null; MinFps = null; Aspect = Codec = null;
    }

    /// <summary>True when something other than the plain sort narrows the library.</summary>
    public bool IsNarrowed => Kind != "all" || Root is not null || Project is not null || Unassigned
        || Color is not null || Near is not null || Source is not null || Discover is not null || Pattern is not null || Tag is not null || HasDetailFilters;

    public string ToQueryString(int limit, int offset)
    {
        var parts = new List<string> { $"sort={Uri.EscapeDataString(Sort)}", $"limit={limit}", $"offset={offset}" };
        void Add(string k, object? v)
        {
            if (v is null) return;
            var text = v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : v.ToString();
            if (!string.IsNullOrEmpty(text)) parts.Add($"{k}={Uri.EscapeDataString(text)}");
        }
        if (Kind != "all") Add("kind", Kind);
        Add("root", Root); Add("project", Project);
        if (Unassigned) Add("unassigned", "true");
        Add("color", Color); Add("near", Near); Add("source", Source); Add("discover", Discover); Add("pattern", Pattern); Add("tag", Tag);
        foreach (var (k, v) in DetailFilters()) Add(k, v);
        return string.Join('&', parts);
    }

    /// <summary>The same filters as a search body, so semantic results stay inside them.</summary>
    public Dictionary<string, object?> ToSearchBody(IEnumerable<string> queries, int limit, int offset)
    {
        var body = new Dictionary<string, object?> { ["qs"] = queries.ToArray(), ["limit"] = limit, ["offset"] = offset };
        if (Kind != "all") body["kinds"] = new[] { Kind };
        if (Root is not null) body["root"] = Root;
        if (Project is not null) body["project"] = Project;
        if (Color is not null) body["color"] = Color;
        if (Source is not null) body["source"] = Source;
        if (Tag is not null) body["tag"] = Tag;
        var f = DetailFilters();
        if (f.Count > 0) body["filters"] = f;
        return body;
    }
}

public sealed record SmartFilterResult(FileQuery Query, string? Kind, string Text, List<string> Understood);

/// <summary>Turns "4K videos of motorcycles" into exact filters plus the leftover words for a semantic search.
/// A port of the web app's parseSmartFilter, so both behave the same.</summary>
public static class SmartFilter
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static SmartFilterResult Parse(string input)
    {
        var text = (input ?? "").Trim().ToLowerInvariant();
        var q = new FileQuery();
        var understood = new List<string>();

        void Take(string pattern, Action<Match> apply)
        {
            var m = Regex.Match(text, pattern, O);
            if (!m.Success) return;
            apply(m);
            text = text.Remove(m.Index, m.Length).Insert(m.Index, " ");
        }
        static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        static double Seconds(string n, string unit) => Num(n) * (unit.StartsWith('h') ? 3600 : unit.StartsWith('m') ? 60 : 1);
        static long Bytes(string n, string unit) => (long)Math.Round(Num(n) * (unit.StartsWith('g') ? 1e9 : 1e6));

        Take(@"\b(?:4k|uhd)\b", _ => { q.MinPixels = 3840L * 2160; understood.Add("4K or higher"); });
        Take(@"\b(?:1080p|full\s*hd)\b", _ => { q.MinPixels = 1920L * 1080; understood.Add("Full HD or higher"); });
        Take(@"\b(?:60\s*fps|60\s*frames?\s*per\s*second)\b", _ => { q.MinFps = 60; understood.Add("60 fps or higher"); });
        Take(@"\b(?:30\s*fps|30\s*frames?\s*per\s*second)\b", _ => { q.MinFps = 30; understood.Add("30 fps or higher"); });
        Take(@"\b(?:vertical|portrait)\b", _ => { q.Aspect = "portrait"; understood.Add("Portrait"); });
        Take(@"\b(?:horizontal|landscape)\b", _ => { q.Aspect = "landscape"; understood.Add("Landscape"); });
        Take(@"\bsquare\b", _ => { q.Aspect = "square"; understood.Add("Square"); });
        Take(@"\b(?:wide|widescreen)\b", _ => { q.Aspect = "wide"; understood.Add("Wide"); });
        Take(@"\b(?:under|shorter than|less than)\s*(\d+(?:\.\d+)?)\s*(seconds?|secs?|minutes?|mins?|hours?|hrs?)\b",
            m => { q.MaxDuration = Seconds(m.Groups[1].Value, m.Groups[2].Value); understood.Add($"Under {m.Groups[1].Value} {m.Groups[2].Value}"); });
        Take(@"\b(?:over|longer than|more than)\s*(\d+(?:\.\d+)?)\s*(seconds?|secs?|minutes?|mins?|hours?|hrs?)\b",
            m => { q.MinDuration = Seconds(m.Groups[1].Value, m.Groups[2].Value); understood.Add($"Over {m.Groups[1].Value} {m.Groups[2].Value}"); });
        Take(@"\b(?:larger than|over|bigger than)\s*(\d+(?:\.\d+)?)\s*(mb|gb)\b",
            m => { q.MinSize = Bytes(m.Groups[1].Value, m.Groups[2].Value.ToLowerInvariant()); understood.Add($"Over {m.Groups[1].Value} {m.Groups[2].Value.ToUpperInvariant()}"); });
        Take(@"\b(?:smaller than|under|less than)\s*(\d+(?:\.\d+)?)\s*(mb|gb)\b",
            m => { q.MaxSize = Bytes(m.Groups[1].Value, m.Groups[2].Value.ToLowerInvariant()); understood.Add($"Under {m.Groups[1].Value} {m.Groups[2].Value.ToUpperInvariant()}"); });

        string? kind = null;
        var km = Regex.Match(text, @"\b(videos?|clips?|footage|images?|photos?|pictures?|audio|songs?|sounds?|tracks?)\b", O);
        if (km.Success)
        {
            var w = km.Value;
            kind = Regex.IsMatch(w, "video|clip|footage", O) ? "video" : Regex.IsMatch(w, "image|photo|picture", O) ? "image" : "audio";
            text = text.Remove(km.Index, km.Length).Insert(km.Index, " ");
            understood.Add(kind == "video" ? "Videos" : kind == "image" ? "Images" : "Audio");
        }
        text = Regex.Replace(text, @"\b(?:show me|find me|find|show|only|files?|of|with|that|are|the|my|all)\b", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return new SmartFilterResult(q, kind, text, understood);
    }
}
