using FileMind.Core;
using Xunit;

namespace FileMind.Core.Tests;

public class StorageInfoTests
{
    private static string Temp()
    {
        var d = Path.Combine(Path.GetTempPath(), "fm-storage-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact] public void MeasuresEachKindAndClearsOnlyWhatIsSafe()
    {
        var root = Temp();
        try
        {
            var data = Path.Combine(root, "data"); var logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(Path.Combine(data, "thumbs")); Directory.CreateDirectory(Path.Combine(data, "tmp")); Directory.CreateDirectory(logs);
            Directory.CreateDirectory(Path.Combine(data, "desktop-session-1"));
            File.WriteAllBytes(Path.Combine(data, "index.db"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(data, "thumbs", "a.jpg"), new byte[500]);
            File.WriteAllBytes(Path.Combine(data, "tmp", "x.bin"), new byte[300]);
            File.WriteAllBytes(Path.Combine(data, "desktop-session-1", "y"), new byte[200]);
            File.WriteAllBytes(Path.Combine(logs, "app.log"), new byte[100]);
            var settings = Path.Combine(root, "settings.json"); File.WriteAllText(settings, "{}");

            var entries = StorageInfo.Describe(data, Path.Combine(root, "models"), Path.Combine(root, "runtime"), logs, settings, root);
            StorageInfo.Measure(entries, data, settings);
            long Of(string n) => entries.First(e => e.Name == n).Bytes;
            Assert.Equal(1000, Of("Search index"));
            Assert.Equal(500, Of("Thumbnails"));
            Assert.Equal(300, Of("Temporary files"));
            Assert.Equal(200, Of("Left over by older versions"));
            Assert.False(entries.First(e => e.Name == "Search index").Clearable);   // the index is never cleared from here
            Assert.False(entries.First(e => e.Name == "Thumbnails").Clearable);

            StorageInfo.Clear(entries.First(e => e.Name == "Temporary files"));
            StorageInfo.Clear(entries.First(e => e.Name == "Left over by older versions"));
            Assert.False(File.Exists(Path.Combine(data, "tmp", "x.bin")));
            Assert.False(File.Exists(Path.Combine(data, "desktop-session-1", "y")));
            Assert.True(File.Exists(Path.Combine(data, "index.db")));
            Assert.True(File.Exists(Path.Combine(data, "thumbs", "a.jpg")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
    public void FormatsSizes(long bytes, string text) => Assert.Equal(text, StorageInfo.Format(bytes));
}
