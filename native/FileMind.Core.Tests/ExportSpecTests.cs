using System.Text.Json;
using FileMind.Core;
using Xunit;

namespace FileMind.Core.Tests;

public class ExportSpecTests
{
    [Fact] public void VideoOffersClipFormatsAndSoundOnly()
    {
        var keys = ExportSpec.FormatsFor(["video", "video"]).Select(f => f.Key).ToList();
        Assert.Equal("same", keys[0]);
        Assert.Contains("mp4", keys);
        Assert.Contains("gif", keys);
        Assert.Contains("mp3", keys);
        Assert.Contains("sheet", keys);
        Assert.DoesNotContain("jpg", keys);
    }

    [Fact] public void PicturesAndSoundsGetTheirOwnFormats()
    {
        Assert.Equal(["same", "jpg", "png", "webp"], ExportSpec.FormatsFor(["image"]).Select(f => f.Key));
        Assert.Equal(["same", "mp3", "wav", "m4a", "flac"], ExportSpec.FormatsFor(["audio"]).Select(f => f.Key));
    }

    [Fact] public void MixedKindsCanOnlyKeepTheirFormat()
    {
        Assert.Equal(["same"], ExportSpec.FormatsFor(["image", "video"]).Select(f => f.Key));
    }

    [Fact] public void OnlyMediaCanBeExported()
    {
        Assert.True(ExportSpec.CanExport("video"));
        Assert.True(ExportSpec.CanExport("image"));
        Assert.True(ExportSpec.CanExport("audio"));
        Assert.False(ExportSpec.CanExport("doc"));
        Assert.False(ExportSpec.CanExport("text"));
    }

    [Fact] public void BodyUsesTheEngineFieldNames()
    {
        var spec = new ExportSpec { Format = "mp4", Start = 42, End = 51, Height = 720, TargetMb = 25, TrimSilence = true, Copy = true };
        var json = JsonSerializer.Serialize(spec.ToBody([3, 4], @"C:\out"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("file_ids").GetArrayLength());
        Assert.Equal("mp4", root.GetProperty("format").GetString());
        Assert.Equal(42, root.GetProperty("start").GetDouble());
        Assert.Equal(51, root.GetProperty("end").GetDouble());
        Assert.Equal(720, root.GetProperty("height").GetInt32());
        Assert.Equal(25, root.GetProperty("target_mb").GetDouble());
        Assert.True(root.GetProperty("trim_silence").GetBoolean());
        Assert.True(root.GetProperty("copy").GetBoolean());
        Assert.Equal(1, root.GetProperty("speed").GetDouble());
        Assert.Equal(@"C:\out", root.GetProperty("destination").GetString());
    }
}
