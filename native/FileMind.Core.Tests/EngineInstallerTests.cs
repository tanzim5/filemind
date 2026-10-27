using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using FileMind.Core;
using Xunit;

namespace FileMind.Core.Tests;

/// <summary>Serves one file over HTTP, with Range support, and can cut the connection part-way.</summary>
internal sealed class FileServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly byte[] _data;
    private readonly long _cutAfter;
    public int Requests;
    public string Url { get; }

    public FileServer(byte[] data, long cutAfter = -1)
    {
        _data = data; _cutAfter = cutAfter;
        var port = new Random().Next(20000, 40000);
        Url = $"http://127.0.0.1:{port}/engine.zip";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            Interlocked.Increment(ref Requests);
            long start = 0;
            var range = ctx.Request.Headers["Range"];
            if (range is not null && range.StartsWith("bytes=")) start = long.Parse(range[6..].TrimEnd('-'));
            ctx.Response.StatusCode = start > 0 ? 206 : 200;
            ctx.Response.ContentLength64 = _data.Length - start;
            var end = _data.Length;
            var cut = _cutAfter >= 0 && start == 0;
            if (cut) end = (int)_cutAfter;
            try
            {
                await ctx.Response.OutputStream.WriteAsync(_data.AsMemory((int)start, end - (int)start));
                if (cut) ctx.Response.Abort(); else ctx.Response.Close();
            }
            catch { }
        }
    }

    public void Dispose() { _listener.Stop(); _listener.Close(); }
}

public class EngineInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filemind-test-" + Guid.NewGuid().ToString("N"));
    public EngineInstallerTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static byte[] MakeZip(params (string name, string text)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, text) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
        return ms.ToArray();
    }

    private static EngineEdition Edition(byte[] zip, string url, string? sha = null) => new()
    {
        Url = url, Size = zip.Length, Folder = "FileMind",
        Sha256 = sha ?? Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant(),
    };

    private static byte[] Padded(int kb)            // incompressible filler so the archive is big enough to be cut in half
    {
        var junk = new byte[kb * 1024];
        new Random(7).NextBytes(junk);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("FileMind/FileMind.exe").Open())) w.Write("exe");
            using var s = zip.CreateEntry("FileMind/blob.bin", CompressionLevel.NoCompression).Open();
            s.Write(junk);
        }
        return ms.ToArray();
    }

    [Fact] public async Task InstallsAndFindsTheExe()
    {
        var zip = MakeZip(("FileMind/FileMind.exe", "hi"), ("FileMind/_internal/x.dll", "dll"));
        using var server = new FileServer(zip);
        var paths = new AppPaths(_root);
        var installer = new EngineInstaller(new HttpClient(), paths);

        var exe = await installer.InstallAsync("cpu", Edition(zip, server.Url), "1.0", null, default);

        Assert.True(File.Exists(exe));
        Assert.Equal(exe, installer.InstalledExe("cpu"));
        Assert.True(File.Exists(Path.Combine(paths.Engine, "cpu", "FileMind", "_internal", "x.dll")));
        Assert.Empty(Directory.GetFiles(paths.Downloads));             // the zip is cleaned up
    }

    [Fact] public async Task RejectsADamagedDownloadAndDoesNotInstall()
    {
        var zip = MakeZip(("FileMind/FileMind.exe", "hi"));
        using var server = new FileServer(zip);
        var installer = new EngineInstaller(new HttpClient(), new AppPaths(_root));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync("cpu", Edition(zip, server.Url, new string('0', 64)), "1.0", null, default));

        Assert.Contains("damaged", ex.Message);
        Assert.Null(installer.InstalledExe("cpu"));
    }

    [Fact] public async Task ResumesAfterAConnectionCut()
    {
        var zip = Padded(600);
        using var server = new FileServer(zip, cutAfter: zip.Length / 2);
        var paths = new AppPaths(_root);
        var installer = new EngineInstaller(new HttpClient(), paths);
        var edition = Edition(zip, server.Url);

        await Assert.ThrowsAnyAsync<Exception>(() => installer.InstallAsync("cpu", edition, "1.0", null, default));
        var part = Directory.GetFiles(paths.Downloads, "*.part").Single();
        Assert.InRange(new FileInfo(part).Length, 1, zip.Length - 1);   // kept what it had

        var exe = await installer.InstallAsync("cpu", edition, "1.0", null, default);
        Assert.True(File.Exists(exe));
        Assert.Equal(2, server.Requests);
    }

    [Fact] public async Task RefusesZipsThatEscapeTheInstallFolder()
    {
        var zip = MakeZip(("FileMind/FileMind.exe", "ok"), ("../evil.txt", "bad"));
        using var server = new FileServer(zip);
        var paths = new AppPaths(_root);
        var installer = new EngineInstaller(new HttpClient(), paths);

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync("cpu", Edition(zip, server.Url), "1.0", null, default));

        Assert.False(File.Exists(Path.Combine(paths.Engine, "evil.txt")));
        Assert.Null(installer.InstalledExe("cpu"));
    }

    [Fact] public async Task AFailedUpdateKeepsTheWorkingInstall()
    {
        var good = MakeZip(("FileMind/FileMind.exe", "v1"));
        using var s1 = new FileServer(good);
        var installer = new EngineInstaller(new HttpClient(), new AppPaths(_root));
        await installer.InstallAsync("cpu", Edition(good, s1.Url), "1.0", null, default);

        var bad = MakeZip(("README.txt", "no exe here"));
        using var s2 = new FileServer(bad);
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync("cpu", Edition(bad, s2.Url), "2.0", null, default));

        Assert.NotNull(installer.InstalledExe("cpu"));
        Assert.Equal("v1", File.ReadAllText(installer.InstalledExe("cpu")!));
    }

    [Fact] public void ManifestParses()
    {
        var m = EngineManifest.Parse("""
            {"version":"1","editions":{"cpu":{"title":"CPU","url":"u","size":5,"sha256":"ab","folder":"F"}},
             "cuda":{"minDriverVersion":12080,"minComputeCapability":7.5}}
            """);
        Assert.Equal(5, m.Editions["cpu"].Size);
        Assert.Equal(7.5, m.Cuda.MinComputeCapability);
    }
}
