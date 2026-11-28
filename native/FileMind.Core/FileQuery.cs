using System.Globalization;
using System.Text.RegularExpressions;

namespace FileMind.Core;

/// <summary>Every way the library can be narrowed, mirroring what the engine's /api/files and /api/search accept.</summary>
public sealed class FileQuery
{
    public string Kind { get; set; } = "all";

    /// <summary>Set by the app from its settings: the plain All page leaves out text, PDFs and sounds.</summary>
    public static bool DefaultMediaOnly { get; set; } = true;

    /// <summary>True on the plain All page (no type, folder, tag, space or other scope), where only pictures and videos are shown.</summary>
    public bool MediaOnlyActive => DefaultMediaOnly && Kind == "all" && Root is null && Project is null && !Unassigned && Color is null
        && Near is null && Source is null && Discover is null && Pattern is null && Tag is null && Label is null;
    public int? Root { get; set; }
    public int? Project { get; set; }
    public bool Unassigned { get; set; }
    public string? Color { get; set; }
    public string? Near { get; set; }
    public string? Source { get; set; }
    public string? Discover { get; set; }
    public string? Pattern { get; set; }
    public string? Tag { get; set; }
    /// <summary>Colour labels to show, comma separated ("red,blue").</summary>
    public string? Label { get; set; }
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

    /// <summary>The person's clock and date order, so "yesterday" and "5/8" mean what they mean to them.</summary>
    public static Dictionary<string, object> Clock() => new()
    {
        ["now"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        ["utc_offset_min"] = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes,
        ["day_first"] = !CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern.TrimStart('\'', ' ').StartsWith("M", StringComparison.OrdinalIgnoreCase),
    };

    public static Dictionary<string, object> AskContext(string text)
    {
        var d = Clock(); d["text"] = text; return d;
    }

    /// <summary>Copies what the engine understood into this query (filters, type, colour, source, favourites) and returns the words that are left to search for.</summary>
    public string Apply(Understood u)
    {
        double? D(string k) => u.Filters.TryGetValue(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetDouble() : null;
        if (D("after") is { } a) After = a;
        if (D("before") is { } b) Before = b;
        if (D("min_size") is { } s1) MinSize = (long)s1;
        if (D("max_size") is { } s2) MaxSize = (long)s2;
        if (D("min_pixels") is { } px) MinPixels = (long)px;
        if (D("min_duration") is { } d1) MinDuration = d1;
        if (D("max_duration") is { } d2) MaxDuration = d2;
        if (D("min_fps") is { } fps) MinFps = fps;
        if (u.Filters.TryGetValue("aspect", out var asp) && asp.ValueKind == System.Text.Json.JsonValueKind.String) Aspect = asp.GetString();
        if (u.Filters.TryGetValue("codec", out var cod) && cod.ValueKind == System.Text.Json.JsonValueKind.String) Codec = cod.GetString();
        if (u.Kinds.Count == 1) Kind = u.Kinds[0];
        if (u.Color is not null) Color = u.Color;
        if (u.Source is not null) Source = u.Source;
        if (u.Favourite) Tag = "Favourites";
        return u.Text;
    }

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
        || Color is not null || Near is not null || Source is not null || Discover is not null || Pattern is not null || Tag is not null || Label is not null || HasDetailFilters;

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
        if (MediaOnlyActive) Add("media", "true");
        Add("root", Root); Add("project", Project);
        if (Unassigned) Add("unassigned", "true");
        Add("color", Color); Add("near", Near); Add("source", Source); Add("discover", Discover); Add("pattern", Pattern); Add("tag", Tag); Add("label", Label);
        foreach (var (k, v) in DetailFilters()) Add(k, v);
        return string.Join('&', parts);
    }

    /// <summary>The same filters as a search body, so semantic results stay inside them.</summary>
    public Dictionary<string, object?> ToSearchBody(IEnumerable<string> queries, int limit, int offset)
    {
        var body = new Dictionary<string, object?> { ["qs"] = queries.ToArray(), ["limit"] = limit, ["offset"] = offset };
        foreach (var (k, v) in Clock()) body[k] = v;
        if (Kind != "all") body["kinds"] = new[] { Kind };
        else if (MediaOnlyActive) body["kinds"] = new[] { "image", "video" };
        if (Root is not null) body["root"] = Root;
        if (Project is not null) body["project"] = Project;
        if (Color is not null) body["color"] = Color;
        if (Source is not null) body["source"] = Source;
        if (Tag is not null) body["tag"] = Tag;
        if (Label is not null) body["label"] = Label;
        var f = DetailFilters();
        if (f.Count > 0) body["filters"] = f;
        return body;
    }
}
