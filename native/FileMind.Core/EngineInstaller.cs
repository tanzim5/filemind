using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace FileMind.Core;

public sealed record InstallProgress(string Stage, long Done, long Total, string Detail = "");

/// <summary>Downloads an engine edition once, checks its SHA-256, and unpacks it. A cut connection resumes from the
/// bytes already on disk, and a half-finished install never replaces a working one.</summary>
public sealed class EngineInstaller(HttpClient http, AppPaths paths)
{
    public string EditionDir(string id) => Path.Combine(paths.Engine, id);

    /// <summary>Path to FileMind.exe for an installed edition, or null when it is not (completely) installed.</summary>
    public string? InstalledExe(string id)
    {
        var dir = EditionDir(id);
        if (!File.Exists(Path.Combine(dir, ".installed"))) return null;
        return Directory.EnumerateFiles(dir, "FileMind.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    public async Task<string> InstallAsync(string id, EngineEdition edition, string version,
                                           IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Downloads);
        var zip = Path.Combine(paths.Downloads, $"{id}-{version}.zip");
        await DownloadAsync(edition, zip, progress, ct);

        progress?.Report(new InstallProgress("verify", 0, 0, "Checking the download"));
        var actual = await Sha256Async(zip, ct);
        if (!actual.Equals(edition.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zip);
            throw new InvalidDataException("The download was damaged in transit. Please try again.");
        }

        var stage = Path.Combine(paths.Engine, $".installing-{id}");
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        Directory.CreateDirectory(stage);
        await Task.Run(() => Unpack(zip, stage, progress, ct), ct);

        if (!Directory.EnumerateFiles(stage, "FileMind.exe", SearchOption.AllDirectories).Any())
            throw new InvalidDataException("The download does not contain FileMind.exe.");
        File.WriteAllText(Path.Combine(stage, ".installed"),
            JsonSerializer.Serialize(new { version, sha256 = edition.Sha256, installed = DateTime.UtcNow }));

        var final = EditionDir(id);
        if (Directory.Exists(final)) Directory.Delete(final, true);
        Directory.Move(stage, final);
        File.Delete(zip);
        progress?.Report(new InstallProgress("done", 1, 1));
        return InstalledExe(id)!;
    }

    private async Task DownloadAsync(EngineEdition edition, string zip, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var part = zip + ".part";
        if (File.Exists(zip) && new FileInfo(zip).Length == edition.Size) return;   // already fetched; verify will vouch for it
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (edition.Size > 0 && have > edition.Size) { File.Delete(part); have = 0; }

        if (edition.Size == 0 || have < edition.Size)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, edition.Url);
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(part); have = 0; }
            else
            {
                response.EnsureSuccessStatusCode();
                if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent) have = 0;   // server ignored the resume
                var total = edition.Size > 0 ? edition.Size : (response.Content.Headers.ContentLength ?? 0) + have;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
                var buffer = new byte[1 << 20];
                long done = have, lastReport = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (done - lastReport >= 512 * 1024) { lastReport = done; progress?.Report(new InstallProgress("download", done, total, Path.GetFileName(zip))); }
                }
                progress?.Report(new InstallProgress("download", done, total, Path.GetFileName(zip)));
            }
        }
        File.Move(part, zip, true);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private static void Unpack(string zipPath, string dest, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        long total = archive.Entries.Sum(e => e.Length), done = 0, lastReport = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(dest, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsafe path in the download: {entry.FullName}");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
            done += entry.Length;
            if (done - lastReport >= 8L * 1024 * 1024) { lastReport = done; progress?.Report(new InstallProgress("unpack", done, total, entry.Name)); }
        }
        progress?.Report(new InstallProgress("unpack", total, total));
    }
}
