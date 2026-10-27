using FileMind.Core;
using Xunit;

namespace FileMind.Core.Tests;

public class GpuProbeTests
{
    private static readonly CudaRequirement Need = new() { MinDriverVersion = 12080, MinComputeCapability = 7.0 };
    private static CudaFacts Gpu(int driver, int major, int minor, string name = "Test GPU", long vram = 12L << 30) =>
        new(driver, [new CudaDevice(name, major, minor, vram)]);

    [Fact] public void NoDriverMeansCpu()
    {
        var info = GpuProbe.Evaluate(null, Need);
        Assert.False(info.CudaUsable);
        Assert.False(info.NvidiaPresent);
        Assert.Equal("cpu", GpuProbe.ChooseEdition("auto", info));
    }

    [Fact] public void ModernCardAndDriverMeansCuda()
    {
        var info = GpuProbe.Evaluate(Gpu(13000, 8, 6, "NVIDIA GeForce RTX 3060"), Need);
        Assert.True(info.CudaUsable);
        Assert.Equal("13.0", info.DriverCuda);
        Assert.Equal("nvidia", GpuProbe.ChooseEdition("auto", info));
        Assert.Contains("RTX 3060", info.Headline);
    }

    [Fact] public void OldDriverIsExplainedNotSilentlyIgnored()
    {
        var info = GpuProbe.Evaluate(Gpu(11040, 8, 6), Need);
        Assert.True(info.NvidiaPresent);
        Assert.False(info.CudaUsable);
        Assert.Contains("12.8", info.Reason);
        Assert.Equal("cpu", GpuProbe.ChooseEdition("auto", info));
    }

    [Fact] public void TooOldCardMeansCpu()
    {
        var info = GpuProbe.Evaluate(Gpu(13000, 6, 1, "GTX 1060"), Need);
        Assert.False(info.CudaUsable);
        Assert.Contains("too old", info.Reason);
    }

    [Fact] public void ExactMinimumDriverIsAccepted() =>
        Assert.True(GpuProbe.Evaluate(Gpu(12080, 7, 5), Need).CudaUsable);

    [Fact] public void BestOfSeveralGpusIsUsed()
    {
        var facts = new CudaFacts(13000, [new CudaDevice("Old", 6, 1, 8L << 30), new CudaDevice("New", 8, 9, 24L << 30)]);
        var info = GpuProbe.Evaluate(facts, Need);
        Assert.True(info.CudaUsable);
        Assert.Equal("New", info.Name);
    }

    [Fact] public void UserCanForceCpuEvenWithCuda()
    {
        var info = GpuProbe.Evaluate(Gpu(13000, 8, 6), Need);
        Assert.Equal("cpu", GpuProbe.ChooseEdition("cpu", info));
    }

    [Fact] public void ForcingNvidiaWithoutCudaFallsBackToCpu() =>
        Assert.Equal("cpu", GpuProbe.ChooseEdition("nvidia", GpuProbe.Evaluate(null, Need)));

    [Fact] public void RealMachineProbeNeverThrows()
    {
        var info = GpuProbe.Detect(Need);          // whatever this machine has, it must answer
        Assert.NotNull(info.Reason);
    }
}
