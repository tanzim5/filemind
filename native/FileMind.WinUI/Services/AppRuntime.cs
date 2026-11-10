using FileMind.Core;

namespace FileMind;

/// <summary>The app's long-lived pieces: settings, hardware, the engine process and its API.</summary>
public sealed class AppRuntime
{
    public static AppRuntime Current { get; } = new();

    public AppPaths Paths { get; } = new();
    public AppSettings Settings { get; }
    public HttpClient Http { get; } = new() { Timeout = Timeout.InfiniteTimeSpan };
    public EngineHost Host { get; } = new();
    public ApiClient? Api { get; private set; }

    public GpuInfo Gpu { get; private set; } = new(false, false, null, "", 0, "");
    /// <summary>"cpu" or "nvidia": the edition this PC should run.</summary>
    public string Edition { get; private set; } = EngineManifest.Cpu;
    /// <summary>Set when running against a source checkout (development) instead of an installed engine.</summary>
    public string? SourceLabel { get; private set; }

    private AppRuntime()
    {
        Settings = AppSettings.Load(Paths);
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("FileMind-Native");
    }

    /// <summary>Looks at the hardware and decides the edition. Quick and offline.</summary>
    public void DetectHardware()
    {
        Gpu = GpuProbe.Detect(new CudaRequirement());
        Edition = GpuProbe.ChooseEdition(Settings.Engine, Gpu);
    }

    /// <summary>Remember the user's preference ("auto", "cpu" or "nvidia") and work out which edition that means here.</summary>
    public void ChooseEdition(string preference)
    {
        Settings.Engine = preference;
        Settings.Save(Paths);
        Edition = GpuProbe.ChooseEdition(preference, Gpu);
    }

    /// <summary>What to run right now, or null when the engine folder is missing from the app.</summary>
    public EngineLaunch? ResolveLaunch()
    {
        var wantInstalled = Environment.GetEnvironmentVariable("FILEMIND_ENGINE") == "installed";
        if (!wantInstalled && EngineLocator.FindSourceCheckout(AppContext.BaseDirectory) is { } source)
        {
            SourceLabel = source.Label;
            return source;
        }
        SourceLabel = null;
        var accel = Settings.Engine switch { "cpu" => "cpu", "nvidia" => "cuda", _ => null };
        return EngineLocator.FindBundled(AppContext.BaseDirectory, Paths, accel);
    }

    public async Task StartEngineAsync(EngineLaunch launch, CancellationToken ct)
    {
        await Host.StartAsync(launch, Paths, TimeSpan.FromMinutes(3), ct);
        Api?.Dispose();
        Api = new ApiClient(Host.BaseUri);
    }

    public void Shutdown()
    {
        Api?.Dispose();
        Host.Dispose();
    }
}
