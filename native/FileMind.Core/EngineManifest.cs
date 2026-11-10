namespace FileMind.Core;

/// <summary>What an NVIDIA card needs for the engine's CUDA PyTorch (keep in step with packaging/runtime-windows.json).</summary>
public sealed class CudaRequirement
{
    /// <summary>Highest CUDA version the driver supports, encoded like 12080 for 12.8.</summary>
    public int MinDriverVersion { get; set; } = 12080;
    public double MinComputeCapability { get; set; } = 7.0;
}

/// <summary>Names for what the engine runs on.</summary>
public static class EngineManifest
{
    public const string Cpu = "cpu";
    public const string Nvidia = "nvidia";
}
