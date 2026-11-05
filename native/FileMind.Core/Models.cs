using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileMind.Core;

public sealed class FileItem
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("ext")] public string Ext { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("mtime")] public double Mtime { get; set; }
    [JsonPropertyName("has_thumb")] public bool HasThumb { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("duration")] public double? Duration { get; set; }
    [JsonPropertyName("score")] public double? Score { get; set; }
    [JsonPropertyName("tw")] public int? ThumbWidth { get; set; }
    [JsonPropertyName("th")] public int? ThumbHeight { get; set; }
    [JsonPropertyName("dom")] public string? Dominant { get; set; }
    [JsonPropertyName("src")] public string? Source { get; set; }
    [JsonPropertyName("fps")] public double? Fps { get; set; }
    [JsonPropertyName("codec")] public string? Codec { get; set; }
    [JsonPropertyName("proposed")] public string? Proposed { get; set; }
    [JsonPropertyName("snippet")] public string? Snippet { get; set; }

    public string SizeText => Size switch
    {
        >= 1L << 30 => $"{Size / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{Size / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{Size / 1024.0:0} KB",
        _ => $"{Size} B",
    };

    public string DurationText => Duration is > 0 and var d
        ? (d >= 3600 ? TimeSpan.FromSeconds(d).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(d).ToString(@"m\:ss"))
        : "";

    public string DimensionsText => Width is > 0 && Height is > 0 ? $"{Width} × {Height}" : "";
    public DateTime Modified => DateTimeOffset.FromUnixTimeSeconds((long)Mtime).LocalDateTime;
}

public sealed class FilesPage
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("files")] public List<FileItem> Files { get; set; } = [];
}

public sealed class SearchResult
{
    [JsonPropertyName("files")] public List<FileItem> Files { get; set; } = [];
}

public sealed class RootFolder
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("files")] public int Files { get; set; }
    [JsonPropertyName("indexed")] public int Indexed { get; set; }
    [JsonPropertyName("errors")] public int Errors { get; set; }
}

public sealed class IndexState
{
    [JsonPropertyName("running")] public bool Running { get; set; }
    [JsonPropertyName("phase")] public string Phase { get; set; } = "idle";
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("done")] public int Done { get; set; }
    [JsonPropertyName("errors")] public int Errors { get; set; }
    [JsonPropertyName("current")] public string Current { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("device")] public string Device { get; set; } = "";
}

public sealed class ModelStatus
{
    [JsonPropertyName("ready")] public bool Ready { get; set; }
    [JsonPropertyName("gpu")] public bool Gpu { get; set; }
    [JsonPropertyName("device")] public string? Device { get; set; }
    /// <summary>"cuda" or "cpu": which PyTorch this engine downloads/uses, and why it chose it.</summary>
    [JsonPropertyName("accel")] public string? Accel { get; set; }
    [JsonPropertyName("accel_reason")] public string? AccelReason { get; set; }
    [JsonPropertyName("runtime_bytes")] public long RuntimeBytes { get; set; }
    [JsonPropertyName("running")] public bool Running { get; set; }
    [JsonPropertyName("done")] public long Done { get; set; }
    [JsonPropertyName("total")] public long Total { get; set; }
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("stage")] public string Stage { get; set; } = "";
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class LibraryStats
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("kinds")] public Dictionary<string, int> Kinds { get; set; } = new();
}
