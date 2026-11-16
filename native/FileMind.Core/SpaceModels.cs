using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileMind.Core;

/// <summary>What the engine says about a set of descriptions: every matching file id (best first), a few to show, and how many fit each.</summary>
public sealed class SpaceMatchResult
{
    [JsonPropertyName("file_ids")] public List<int> FileIds { get; set; } = [];
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("per_description")] public List<int> PerDescription { get; set; } = [];
    [JsonPropertyName("preview")] public List<FileItem> Preview { get; set; } = [];
}

/// <summary>Small rules for the "spaces from descriptions" flow, kept out of the UI so they can be tested.</summary>
public static class SpaceDescriptions
{
    public const int MaxDescriptions = 8;

    /// <summary>Trimmed, empty ones dropped, duplicates (ignoring case) dropped, at most <see cref="MaxDescriptions"/>.</summary>
    public static List<string> Clean(IEnumerable<string> raw)
    {
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var list = new List<string>();
        foreach (var r in raw)
        {
            var t = (r ?? "").Trim();
            if (t.Length == 0 || t.Length > 200 || !seen.Add(t)) continue;
            list.Add(t);
            if (list.Count == MaxDescriptions) break;
        }
        return list;
    }

    /// <summary>The name to use: what was typed, else the first description with a capital letter.</summary>
    public static string NameFor(string? typed, IReadOnlyList<string> descriptions)
    {
        var name = (typed ?? "").Trim();
        if (name.Length > 0) return name;
        if (descriptions.Count == 0) return "";
        var first = descriptions[0].Trim();
        return char.ToUpperInvariant(first[0]) + first[1..];
    }
}

/// <summary>Suggested spaces the user dismissed, kept on this PC so they do not come back. A proposal is recognised by its
/// name (the engine names a group after what it holds), ignoring case and spacing.</summary>
public sealed class DismissedSpaces
{
    private readonly string _file;
    private readonly HashSet<string> _names;

    public DismissedSpaces(string file)
    {
        _file = file;
        try { _names = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(file)) ?? []; }
        catch { _names = []; }
    }

    public static string Key(string name) => string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public bool IsDismissed(string name) => _names.Contains(Key(name));

    public void Dismiss(string name)
    {
        if (!_names.Add(Key(name))) return;
        try { File.WriteAllText(_file, JsonSerializer.Serialize(_names.Order().ToList())); } catch { /* remembered for this session at least */ }
    }

    /// <summary>The proposals that were not dismissed, best first, at most <paramref name="max"/>.</summary>
    public List<ClusterProposal> Visible(IEnumerable<ClusterProposal> proposals, int max = 6) =>
        proposals.Where(p => !IsDismissed(p.Name)).Take(max).ToList();
}
