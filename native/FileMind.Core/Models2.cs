using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileMind.Core;

public sealed class ColorSwatch
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("swatch")] public string Swatch { get; set; } = "#888888";
    [JsonPropertyName("count")] public int Count { get; set; }
}

public sealed class SourceEntry
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("color")] public string Color { get; set; } = "#888888";
    [JsonPropertyName("count")] public int Count { get; set; }
}

public sealed class SourcesResponse
{
    [JsonPropertyName("sources")] public List<SourceEntry> Sources { get; set; } = [];
}

public sealed class MoodEntry
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("query")] public string Query { get; set; } = "";
}

public sealed class CoverRef
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
}

public sealed class ProjectEntry
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("smart")] public bool Smart { get; set; }
    [JsonPropertyName("cover")] public List<CoverRef> Cover { get; set; } = [];
}

public sealed class DiscoverGroup
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("sample")] public List<CoverRef> Sample { get; set; } = [];
}

public sealed class DiscoverResponse
{
    [JsonPropertyName("groups")] public List<DiscoverGroup> Groups { get; set; } = [];
    [JsonPropertyName("building")] public bool Building { get; set; }
}

public sealed class DuplicatesResponse
{
    [JsonPropertyName("exact")] public List<List<FileItem>> Exact { get; set; } = [];
    [JsonPropertyName("visual")] public List<List<FileItem>> Visual { get; set; } = [];
    [JsonPropertyName("total_exact")] public int TotalExact { get; set; }
    [JsonPropertyName("total_visual")] public int TotalVisual { get; set; }
}

public sealed class ProjectRef
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

/// <summary>Everything the engine knows about one file: palette, the spaces it belongs to, where it lives.</summary>
public sealed class FileDetails
{
    [JsonPropertyName("colors")] public List<string> Colors { get; set; } = [];
    [JsonPropertyName("projects")] public List<ProjectRef> Projects { get; set; } = [];
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    [JsonPropertyName("tags")] public List<JsonElement> Tags { get; set; } = [];
    [JsonPropertyName("dimensions")] public List<int>? Dimensions { get; set; }
    [JsonPropertyName("proposed")] public string? Proposed { get; set; }
    [JsonPropertyName("meta")] public FileMeta? Meta { get; set; }
    [JsonPropertyName("swatches")] public List<SwatchShare> Swatches { get; set; } = [];
}

public sealed class FileMeta
{
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("prompt")] public string? Prompt { get; set; }
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("author")] public string? Author { get; set; }
}

public sealed class SwatchShare
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("share")] public double Share { get; set; }
    [JsonPropertyName("swatch")] public string Swatch { get; set; } = "#888888";
}

public sealed class WaveData
{
    [JsonPropertyName("peaks")] public List<double> Peaks { get; set; } = [];
    [JsonPropertyName("duration")] public double Duration { get; set; }
}

public sealed class MomentSegment
{
    [JsonPropertyName("start")] public double Start { get; set; }
    [JsonPropertyName("end")] public double End { get; set; }
}

public sealed class MomentState
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("done")] public int Done { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("segments")] public List<MomentSegment> Segments { get; set; } = [];
}

public sealed class NameGroup
{
    [JsonPropertyName("pattern")] public string Pattern { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("names")] public List<string> Names { get; set; } = [];
    [JsonPropertyName("file_ids")] public List<int> FileIds { get; set; } = [];
}

public sealed class NameGroupsResponse
{
    [JsonPropertyName("groups")] public List<NameGroup> Groups { get; set; } = [];
}

public sealed class IdsResponse
{
    [JsonPropertyName("ids")] public List<int> Ids { get; set; } = [];
}

public sealed class TranscriptState
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
}

public sealed class IdeaEntry
{
    [JsonPropertyName("q")] public string Q { get; set; } = "";
    [JsonPropertyName("top")] public List<CoverRef> Top { get; set; } = [];
}

public sealed class IdeasResponse
{
    [JsonPropertyName("ideas")] public List<IdeaEntry> Ideas { get; set; } = [];
}

/// <summary>A spoken search: the id to search with, how long it was, and words it sounds close to.</summary>
public sealed class VoiceResult
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("secs")] public double Secs { get; set; }
    [JsonPropertyName("hint")] public List<string> Hint { get; set; } = [];
}
