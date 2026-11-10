using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace FileMind.Core;

/// <summary>How to start the engine: an installed FileMind.exe, or a source checkout run with its own Python.</summary>
public sealed record EngineLaunch(string FileName, IReadOnlyList<string> PrefixArgs, string WorkingDir,
                                  IReadOnlyDictionary<string, string> Env, string Label);

public static class EngineLocator
{
    /// <summary>Finds a source checkout (filemind.py plus .venv) above <paramref name="startDir"/>. Used while developing,
    /// so the native app can run against your working copy and your existing library without installing anything.</summary>
    public static EngineLaunch? FindSourceCheckout(string startDir)
    {
        for (var dir = new DirectoryInfo(startDir); dir is not null; dir = dir.Parent)
        {
            var script = Path.Combine(dir.FullName, "filemind.py");
            var python = Path.Combine(dir.FullName, ".venv", "Scripts", "python.exe");
            if (File.Exists(script) && File.Exists(python))
                return new EngineLaunch(python, [script], dir.FullName, new Dictionary<string, string>(), "source checkout");
        }
        return null;
    }

    /// <summary>The engine shipped inside the release zip, in engine\ beside the app. It downloads the right PyTorch
    /// (CUDA for a usable NVIDIA card, otherwise CPU) on first run; <paramref name="accel"/> forces "cpu" or "cuda".</summary>
    public static EngineLaunch? FindBundled(string appDir, AppPaths paths, string? accel)
    {
        var exe = Path.Combine(appDir, "engine", "FileMind.exe");
        if (!File.Exists(exe)) return null;
        var env = new Dictionary<string, string>
        {
            ["FILEMIND_DATA"] = paths.Data,
            ["FILEMIND_MODELS"] = paths.Models,
            ["FILEMIND_RUNTIME"] = paths.Runtime,
        };
        if (accel is "cpu" or "cuda") env["FILEMIND_ACCEL"] = accel;
        return new EngineLaunch(exe, [], Path.GetDirectoryName(exe)!, env, "bundled engine");
    }

}

/// <summary>Runs the engine on a private local port and stops it with the app, even if the app is killed.</summary>
public sealed class EngineHost : IDisposable
{
    private Process? _process;
    private IntPtr _job;
    private StreamWriter? _log;
    public Uri BaseUri { get; private set; } = new("http://127.0.0.1:0");
    public bool IsRunning => _process is { HasExited: false };
    public string? ExitMessage { get; private set; }

    public async Task StartAsync(EngineLaunch launch, AppPaths paths, TimeSpan timeout, CancellationToken ct)
    {
        Stop();
        var port = FreePort();
        BaseUri = new Uri($"http://127.0.0.1:{port}");
        Directory.CreateDirectory(paths.Logs);
        _log = new StreamWriter(Path.Combine(paths.Logs, "engine.log"), true) { AutoFlush = true };
        _log.WriteLine($"--- {DateTime.Now:u} starting {launch.Label}: {launch.FileName}");

        var info = new ProcessStartInfo(launch.FileName)
        {
            WorkingDirectory = launch.WorkingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in launch.PrefixArgs) info.ArgumentList.Add(a);
        foreach (var a in new[] { "--no-window", "--port", port.ToString() }) info.ArgumentList.Add(a);
        foreach (var (k, v) in launch.Env) info.Environment[k] = v;
        info.Environment["PYTHONUTF8"] = "1";

        _process = Process.Start(info) ?? throw new InvalidOperationException("The engine could not be started.");
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) _log?.WriteLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _log?.WriteLine(e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        JoinJob(_process);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                ExitMessage = $"The engine stopped on startup (exit code {_process.ExitCode}). See {Path.Combine(paths.Logs, "engine.log")}.";
                throw new InvalidOperationException(ExitMessage);
            }
            try { if ((await http.GetAsync(new Uri(BaseUri, "/api/model"), ct)).IsSuccessStatusCode) return; }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("The engine did not start in time.");
    }

    public void Stop()
    {
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
        _process?.Dispose();
        _process = null;
        _log?.Dispose();
        _log = null;
    }

    public void Dispose()
    {
        Stop();
        if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    // A Windows job object that kills its processes when the last handle closes: if the app crashes or is ended from
    // Task Manager, the engine (and its ffmpeg children) go with it instead of lingering.
    private void JoinJob(Process p)
    {
        if (!OperatingSystem.IsWindows()) return;
        _job = CreateJobObject(IntPtr.Zero, null);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ } };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (SetInformationJobObject(_job, 9 /* ExtendedLimitInformation */, ptr, (uint)size))
                AssignProcessToJobObject(_job, p.Handle);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
