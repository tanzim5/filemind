using System.Text.Json.Serialization;

namespace FileMind.Core;

/// <summary>What to make from a file: a clip, another format, a smaller copy. The engine fills in everything left out.</summary>
public sealed class ExportSpec
{
    /// <summary>"same" keeps the file's own type; otherwise mp4, webm, gif, mp3, wav, m4a, flac, jpg, png, webp or sheet.</summary>
    public string Format { get; set; } = "same";
    public double? Start { get; set; }
    public double? End { get; set; }
    /// <summary>Largest height in pixels; never scaled up.</summary>
    public int? Height { get; set; }
    /// <summary>Fit the result under this many megabytes.</summary>
    public double? TargetMb { get; set; }
    public int Rotate { get; set; }
    public double Speed { get; set; } = 1;
    public bool Vertical { get; set; }
    public bool Normalize { get; set; }
    public bool TrimSilence { get; set; }
    public bool Mute { get; set; }
    /// <summary>Cut without re-encoding: instant and lossless, but the cut lands on the nearest keyframe.</summary>
    public bool Copy { get; set; }

    public Dictionary<string, object?> ToBody(IEnumerable<int> fileIds, string? destination) => new()
    {
        ["file_ids"] = fileIds.ToArray(), ["destination"] = destination, ["format"] = Format,
        ["start"] = Start, ["end"] = End, ["height"] = Height, ["target_mb"] = TargetMb,
        ["rotate"] = Rotate, ["speed"] = Speed, ["vertical"] = Vertical, ["normalize"] = Normalize,
        ["trim_silence"] = TrimSilence, ["mute"] = Mute, ["copy"] = Copy,
    };

    /// <summary>The formats that make sense for a set of files (by kind), most useful first. "same" is always there.</summary>
    public static IReadOnlyList<(string Key, string Label)> FormatsFor(IEnumerable<string> kinds)
    {
        var set = kinds.Distinct().ToList();
        var list = new List<(string, string)> { ("same", "Keep the format") };
        if (set.Count != 1) return list;
        list.AddRange(set[0] switch
        {
            "video" => new (string, string)[] { ("mp4", "MP4 video"), ("webm", "WebM video"), ("gif", "GIF"), ("mp3", "MP3, sound only"), ("wav", "WAV, sound only"), ("sheet", "Contact sheet (one picture)") },
            "audio" => [("mp3", "MP3"), ("wav", "WAV"), ("m4a", "M4A"), ("flac", "FLAC")],
            "image" => [("jpg", "JPG"), ("png", "PNG"), ("webp", "WebP")],
            _ => [],
        });
        return list;
    }

    /// <summary>Only pictures, videos and sounds can be exported.</summary>
    public static bool CanExport(string kind) => kind is "video" or "audio" or "image";
}

public sealed class ExportJob
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("file_id")] public int FileId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("output")] public string Output { get; set; } = "";
    [JsonPropertyName("format")] public string Format { get; set; } = "";
    /// <summary>queued, running, cancelling, done, error or cancelled.</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("progress")] public double Progress { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    public bool Active => Status is "queued" or "running" or "cancelling";
}

public sealed class ExportJobs
{
    [JsonPropertyName("jobs")] public List<ExportJob> Jobs { get; set; } = [];
}
