namespace FileMind.Core;

/// <summary>One kind of data File Mind keeps on disk: what it is for, where it is and how much space it takes.</summary>
public sealed class StorageEntry
{
    public string Name { get; init; } = "";
    public string Purpose { get; init; } = "";
    /// <summary>The folder to open (or the folder holding the file).</summary>
    public string Path { get; init; } = "";
    public long Bytes { get; set; }
    /// <summary>Files and folders that can be deleted safely; empty when this entry should not be cleared from here.</summary>
    public IReadOnlyList<string> ClearPaths { get; init; } = [];
    /// <summary>What happens after clearing, shown to the user ("Rebuilt when needed.").</summary>
    public string ClearNote { get; init; } = "";
    public bool Clearable => ClearPaths.Count > 0;
}

/// <summary>Adds up what the app, its engine and its data take on this PC, so none of it is a mystery.</summary>
public static class StorageInfo
{
    /// <summary>The standard list. Folders that do not exist are left out. Sizes are filled in by <see cref="Measure"/>.</summary>
    public static List<StorageEntry> Describe(string data, string models, string runtime, string logs, string settingsFile, string appDir)
    {
        var d = (string n) => System.IO.Path.Combine(data, n);
        var list = new List<StorageEntry>
        {
            new() { Name = "Search index", Purpose = "Everything File Mind knows about your files: names, sizes, colours, labels and the vectors that make search work. Forgetting a folder removes its part.", Path = data,
                    ClearPaths = [] },
            new() { Name = "Thumbnails", Purpose = "Small previews shown in the library. They are made again when a file is read again.", Path = d("thumbs") },
            new() { Name = "Waveforms and transcripts", Purpose = "Sound pictures and speech-to-text results for audio and video.", Path = data,
                    ClearPaths = [d("waves"), d("transcripts")], ClearNote = "Made again the next time you open the file." },
            new() { Name = "Temporary files", Purpose = "Short-lived working files, such as frames cut for a search and unfinished exports.", Path = d("tmp"),
                    ClearPaths = [d("tmp")], ClearNote = "Nothing is lost." },
            new() { Name = "Logs", Purpose = "What the app and the engine wrote down, for finding problems.", Path = logs,
                    ClearPaths = [logs, d("filemind.log"), d("filemind.log.1"), d("server.log"), d("server.err"), d("server.err.log")], ClearNote = "Fresh logs start at once." },
            new() { Name = "AI models", Purpose = "The search model and the speech models, downloaded once from Hugging Face.", Path = models },
            new() { Name = "Engine runtime", Purpose = "PyTorch and the engine's libraries, downloaded once. The CUDA version is large.", Path = runtime },
            new() { Name = "Left over by older versions", Purpose = "Folders an earlier version of File Mind created and no longer uses.", Path = data,
                    ClearPaths = Leftovers(data), ClearNote = "These are no longer used." },
            new() { Name = "App", Purpose = "The File Mind program itself (read only here). Uninstall it from Windows.", Path = appDir },
            new() { Name = "Settings", Purpose = "Your choices: layout, theme, folders panel, sidebar order.", Path = System.IO.Path.GetDirectoryName(settingsFile) ?? data },
        };
        // Index files are several names in one folder: count exactly those.
        return list;
    }

    private static List<string> Leftovers(string data)
    {
        var found = new List<string>();
        try
        {
            if (Directory.Exists(data))
                foreach (var dir in Directory.EnumerateDirectories(data))
                {
                    var name = System.IO.Path.GetFileName(dir);
                    if (name.StartsWith("desktop-session-", StringComparison.OrdinalIgnoreCase) || name == "webview") found.Add(dir);
                }
        }
        catch (Exception) { }
        return found;
    }

    /// <summary>Fills in each entry's size. Slow on a big library, so call it off the UI thread.</summary>
    public static void Measure(List<StorageEntry> entries, string data, string settingsFile)
    {
        foreach (var e in entries)
        {
            e.Bytes = e.Name switch
            {
                "Search index" => Sum(System.IO.Directory.Exists(data) ? System.IO.Directory.EnumerateFiles(data, "*", SearchOption.TopDirectoryOnly)
                                      .Where(f => !f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".err", StringComparison.OrdinalIgnoreCase)
                                                  && !f.EndsWith(".1", StringComparison.OrdinalIgnoreCase)) : []),
                "Settings" => Sum(new[] { settingsFile }),
                _ when e.ClearPaths.Count > 0 => e.ClearPaths.Sum(SizeOf),
                _ => SizeOf(e.Path),
            };
        }
    }

    public static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                total += f.Length;
            return total;
        }
        catch (Exception) { return 0; }
    }

    private static long Sum(IEnumerable<string> files)
    {
        long n = 0;
        foreach (var f in files) { try { if (File.Exists(f)) n += new FileInfo(f).Length; } catch (Exception) { } }
        return n;
    }

    /// <summary>Deletes what <paramref name="entry"/> lists as safe to clear. A file or folder that is in use is skipped.</summary>
    public static long Clear(StorageEntry entry)
    {
        long freed = 0;
        foreach (var p in entry.ClearPaths)
        {
            freed += SizeOf(p);
            try
            {
                if (File.Exists(p)) File.Delete(p);
                else if (Directory.Exists(p))
                {
                    foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)) { try { File.Delete(f); } catch (Exception) { } }
                    foreach (var dir in Directory.EnumerateDirectories(p, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length)) { try { Directory.Delete(dir); } catch (Exception) { } }
                }
            }
            catch (Exception) { }
        }
        return freed;
    }

    public static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };
}
