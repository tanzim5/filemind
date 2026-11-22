using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace FileMind.Core;

/// <summary>How this copy of File Mind was installed, which decides who updates it.</summary>
public enum InstallMode
{
    /// <summary>Microsoft Store: the Store updates it. File Mind never replaces its own files.</summary>
    Store,
    /// <summary>winget: "winget upgrade" updates it.</summary>
    Winget,
    /// <summary>An unzipped folder: File Mind can download the new zip, check it and swap the folder itself.</summary>
    Zip,
}

public sealed record UpdateInfo(string Version, string PageUrl, string? ZipUrl, string? Sha256, string Notes);

/// <summary>Looks for a newer release on GitHub and, for a zip install, replaces the app folder with it.
/// A download is only used when its SHA-256 matches the one the release publishes.</summary>
public static class UpdateService
{
    public const string LatestApi = "https://api.github.com/repos/tanzim5/filemind/releases/latest";
    public const string ZipName = "FileMind-Windows.zip";
    public const string WingetId = "Tanzim.FileMind";

    public static InstallMode DetectMode(string appDir)
    {
        var p = appDir.Replace('/', '\\');
        if (p.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase)) return InstallMode.Store;
        if (p.Contains("\\Microsoft\\WinGet\\", StringComparison.OrdinalIgnoreCase)) return InstallMode.Winget;
        return InstallMode.Zip;
    }

    /// <summary>True when <paramref name="latest"/> (like "v0.12.0") is a higher version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string current, string latest)
    {
        static int[]? Parse(string v)
        {
            v = v.Trim().TrimStart('v', 'V');
            var core = new string(v.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Any(p => !int.TryParse(p, out _))) return null;
            return parts.Select(int.Parse).Concat(Enumerable.Repeat(0, 4)).Take(4).ToArray();
        }
        var a = Parse(current); var b = Parse(latest);
        if (a is null || b is null) return false;
        for (var i = 0; i < 4; i++) if (a[i] != b[i]) return b[i] > a[i];
        return false;
    }

    /// <summary>Reads GitHub's "latest release". Null when there is nothing newer (or it cannot be reached).</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, string current, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            req.Headers.UserAgent.ParseAdd("FileMind-Updater");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return Parse(doc.RootElement, current);
        }
        catch (Exception) { return null; }
    }

    public static UpdateInfo? Parse(JsonElement release, string current)
    {
        var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        if (!IsNewer(current, tag)) return null;
        string? zip = null, sha = null, shaUrl = null;
        if (release.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString();
                if (name == ZipName)
                {
                    zip = url;
                    if (a.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        sha = digest[7..];
                }
                else if (name == ZipName + ".sha256") shaUrl = url;
            }
        // A published checksum file wins if the API gave no digest; resolved lazily by the caller through ShaUrl.
        var page = release.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        var notes = release.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        return new UpdateInfo(tag.TrimStart('v', 'V'), page, zip, sha ?? (shaUrl is null ? null : "url:" + shaUrl), notes);
    }

    /// <summary>Downloads the zip and checks it against the published SHA-256. Throws if they differ or none is published.</summary>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo info, string tempDir, IProgress<double>? progress, CancellationToken ct)
    {
        if (info.ZipUrl is null || info.Sha256 is null) throw new InvalidOperationException("This release has no checksum to verify, so File Mind will not install it by itself.");
        var expected = info.Sha256;
        if (expected.StartsWith("url:"))
            expected = (await http.GetStringAsync(expected[4..], ct)).Trim().Split(' ', '\t', '\n')[0];
        Directory.CreateDirectory(tempDir);
        var file = Path.Combine(tempDir, ZipName);
        using (var resp = await http.GetAsync(info.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(file);
            var buf = new byte[1 << 20]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n; if (total > 0) progress?.Report((double)done / total);
            }
        }
        if (!VerifyHash(file, expected)) { File.Delete(file); throw new InvalidDataException("The download did not match its checksum, so it was thrown away."); }
        return file;
    }

    public static bool VerifyHash(string file, string expectedHex)
    {
        using var s = File.OpenRead(file);
        return string.Equals(Convert.ToHexString(SHA256.HashData(s)), expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Unpacks the zip next to the app folder (so the swap is a rename) and starts the helper that waits for this
    /// process to exit, swaps the folders and starts the new version. Throws when the app folder cannot be written.</summary>
    public static void StageAndRestart(string zipFile, string appDir, int processId, string exeName = "FileMind.exe")
    {
        appDir = appDir.TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(appDir) ?? throw new InvalidOperationException("The app folder has no parent.");
        var staging = Path.Combine(parent, ".filemind-update");
        var next = appDir + ".new";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        if (Directory.Exists(next)) Directory.Delete(next, true);
        ZipFile.ExtractToDirectory(zipFile, staging);
        var root = Directory.GetFiles(staging, exeName).Length > 0 ? staging
                 : Directory.GetDirectories(staging).FirstOrDefault(d => File.Exists(Path.Combine(d, exeName)))
                   ?? throw new InvalidDataException("The download does not contain " + exeName + ".");
        Directory.Move(root, next);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        var script = Path.Combine(Path.GetTempPath(), "filemind-update.ps1");
        File.WriteAllText(script, SwapScript);
        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                             "-ProcId", processId.ToString(), "-Target", appDir, "-Next", next, "-Exe", exeName },
            UseShellExecute = false, CreateNoWindow = true,
        });
    }

    public const string SwapScript = """
        param($ProcId, $Target, $Next, $Exe)
        try { Wait-Process -Id $ProcId -Timeout 120 -ErrorAction SilentlyContinue } catch {}
        $old = "$Target.old"
        if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force -ErrorAction SilentlyContinue }
        $leaf = Split-Path $Target -Leaf
        try {
            Rename-Item -LiteralPath $Target -NewName "$leaf.old" -ErrorAction Stop
            Rename-Item -LiteralPath $Next -NewName $leaf -ErrorAction Stop
        } catch {
            if ((Test-Path -LiteralPath $old) -and -not (Test-Path -LiteralPath $Target)) { Rename-Item -LiteralPath $old -NewName $leaf }
            Start-Process -FilePath (Join-Path $Target $Exe)
            exit 1
        }
        Start-Process -FilePath (Join-Path $Target $Exe)
        Remove-Item -LiteralPath $old -Recurse -Force -ErrorAction SilentlyContinue
        """;
}
