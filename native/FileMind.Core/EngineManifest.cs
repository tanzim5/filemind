using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileMind.Core;

public sealed class EngineEdition
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    /// <summary>Folder inside the zip that holds FileMind.exe.</summary>
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
}

public sealed class CudaRequirement
{
    /// <summary>Highest CUDA version the driver supports, encoded like 12080 for 12.8.</summary>
    [JsonPropertyName("minDriverVersion")] public int MinDriverVersion { get; set; } = 12080;
    [JsonPropertyName("minComputeCapability")] public double MinComputeCapability { get; set; } = 7.0;
}

public sealed class EngineManifest
{
    public const string Cpu = "cpu";
    public const string Nvidia = "nvidia";

    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("editions")] public Dictionary<string, EngineEdition> Editions { get; set; } = new();
    [JsonPropertyName("cuda")] public CudaRequirement Cuda { get; set; } = new();

    public static EngineManifest Parse(string json) =>
        JsonSerializer.Deserialize<EngineManifest>(json) ?? throw new InvalidDataException("Empty engine manifest");

    public static EngineManifest Load(string path) => Parse(File.ReadAllText(path));
}
