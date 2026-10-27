using System.Runtime.InteropServices;

namespace FileMind.Core;

/// <summary>One CUDA device as reported by the NVIDIA driver.</summary>
public sealed record CudaDevice(string Name, int ComputeMajor, int ComputeMinor, long VramBytes)
{
    public double Capability => ComputeMajor + ComputeMinor / 10.0;
}

/// <summary>What the NVIDIA driver reported. <c>null</c> from <see cref="NvCuda.Query"/> means no driver at all.</summary>
public sealed record CudaFacts(int DriverVersion, IReadOnlyList<CudaDevice> Devices);

public sealed record GpuInfo(bool NvidiaPresent, bool CudaUsable, string? Name, string DriverCuda, long VramBytes, string Reason)
{
    public string Headline => CudaUsable
        ? $"{Name} · CUDA {DriverCuda}" + (VramBytes > 0 ? $" · {VramBytes / (1024.0 * 1024 * 1024):0.#} GB" : "")
        : NvidiaPresent ? $"{Name} (not usable for CUDA)" : "No NVIDIA GPU";
}

public static class GpuProbe
{
    public static GpuInfo Detect(CudaRequirement requirement)
    {
        try { return Evaluate(NvCuda.Query(), requirement); }
        catch (Exception e) { return new GpuInfo(false, false, null, "", 0, $"The GPU check failed ({e.Message}), so the CPU edition is used."); }
    }

    /// <summary>The decision itself, kept apart from the driver calls so it can be tested.</summary>
    public static GpuInfo Evaluate(CudaFacts? facts, CudaRequirement need)
    {
        if (facts is null || facts.Devices.Count == 0)
            return new GpuInfo(false, false, null, "", 0,
                facts is null ? "No NVIDIA driver was found, so the CPU edition is the right one."
                              : "The NVIDIA driver reports no GPU.");

        var best = facts.Devices.OrderByDescending(d => d.Capability).ThenByDescending(d => d.VramBytes).First();
        var cuda = $"{facts.DriverVersion / 1000}.{facts.DriverVersion % 1000 / 10}";
        if (facts.DriverVersion < need.MinDriverVersion)
            return new GpuInfo(true, false, best.Name, cuda, best.VramBytes,
                $"Your NVIDIA driver supports CUDA {cuda}; the GPU edition needs {need.MinDriverVersion / 1000}.{need.MinDriverVersion % 1000 / 10} or newer. Updating the driver unlocks it.");
        if (best.Capability < need.MinComputeCapability)
            return new GpuInfo(true, false, best.Name, cuda, best.VramBytes,
                $"{best.Name} is too old for the GPU edition (compute {best.Capability:0.0}, needs {need.MinComputeCapability:0.0} or newer).");
        return new GpuInfo(true, true, best.Name, cuda, best.VramBytes, "CUDA is available.");
    }

    /// <summary>"auto" gives the CUDA edition when it is usable, otherwise CPU. An explicit "nvidia" on a machine
    /// without usable CUDA also falls back to CPU, rather than installing something that cannot run.</summary>
    public static string ChooseEdition(string preference, GpuInfo gpu) => preference switch
    {
        EngineManifest.Cpu => EngineManifest.Cpu,
        _ => gpu.CudaUsable ? EngineManifest.Nvidia : EngineManifest.Cpu,
    };
}

/// <summary>Asks nvcuda.dll (installed with the NVIDIA driver) directly: no nvidia-smi and no CUDA toolkit needed.</summary>
public static class NvCuda
{
    [DllImport("nvcuda.dll")] private static extern int cuInit(uint flags);
    [DllImport("nvcuda.dll")] private static extern int cuDriverGetVersion(out int version);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGetCount(out int count);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGet(out int device, int ordinal);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGetName([Out] byte[] name, int len, int device);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGetAttribute(out int value, int attribute, int device);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceTotalMem_v2(out nuint bytes, int device);

    private const int CcMajor = 75, CcMinor = 76;

    public static CudaFacts? Query()
    {
        if (!OperatingSystem.IsWindows() || !NativeLibrary.TryLoad("nvcuda.dll", out var lib)) return null;
        NativeLibrary.Free(lib);
        if (cuInit(0) != 0 || cuDriverGetVersion(out var version) != 0) return null;
        var devices = new List<CudaDevice>();
        if (cuDeviceGetCount(out var count) == 0)
            for (var i = 0; i < count; i++)
            {
                if (cuDeviceGet(out var dev, i) != 0) continue;
                var name = new byte[256];
                cuDeviceGetName(name, name.Length, dev);
                cuDeviceGetAttribute(out var major, CcMajor, dev);
                cuDeviceGetAttribute(out var minor, CcMinor, dev);
                cuDeviceTotalMem_v2(out var mem, dev);
                devices.Add(new CudaDevice(System.Text.Encoding.ASCII.GetString(name).TrimEnd('\0'), major, minor, (long)mem));
            }
        return new CudaFacts(version, devices);
    }
}
