using System.Text.Json;
using FileMind.Core;
using Xunit;

namespace FileMind.Core.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("0.9.0", "v0.10.0", true)]
    [InlineData("0.10.0", "v0.9.9", false)]
    [InlineData("0.12.0", "0.12.0", false)]
    [InlineData("0.12.0", "v1.0.0", true)]
    [InlineData("0.12.0", "garbage", false)]
    [InlineData("0.12.0", "v0.12.0.1", true)]
    public void ComparesVersions(string current, string latest, bool newer) => Assert.Equal(newer, UpdateService.IsNewer(current, latest));

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Tanzim.FileMind_0.12.0.0_x64__abc\", InstallMode.Store)]
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\WinGet\Packages\Tanzim.FileMind_x\FileMind", InstallMode.Winget)]
    [InlineData(@"D:\Tools\FileMind", InstallMode.Zip)]
    public void KnowsHowItWasInstalled(string dir, InstallMode mode) => Assert.Equal(mode, UpdateService.DetectMode(dir));

    [Fact] public void ReadsTheLatestReleaseAndItsChecksum()
    {
        var json = """
        {"tag_name":"v0.12.0","html_url":"https://github.com/x/y/releases/tag/v0.12.0","body":"notes","prerelease":false,
         "assets":[{"name":"FileMind-Windows.zip","browser_download_url":"https://x/zip","digest":"sha256:ABCDEF"}]}
        """;
        using var doc = JsonDocument.Parse(json);
        var info = UpdateService.Parse(doc.RootElement, "0.11.0");
        Assert.NotNull(info);
        Assert.Equal("0.12.0", info!.Version);
        Assert.Equal("https://x/zip", info.ZipUrl);
        Assert.Equal("ABCDEF", info.Sha256);
        Assert.Null(UpdateService.Parse(doc.RootElement, "0.12.0"));
    }

    [Fact] public void FindsTheInstallerAndItsChecksum()
    {
        var json = """
        {"tag_name":"v0.16.0","html_url":"h","body":"","prerelease":false,"assets":[
         {"name":"FileMind-Windows.zip","browser_download_url":"https://x/zip","digest":"sha256:AA"},
         {"name":"FileMind-x64.msi","browser_download_url":"https://x/msi"},
         {"name":"FileMind-x64.msi.sha256","browser_download_url":"https://x/msi.sha256"}]}
        """;
        using var doc = JsonDocument.Parse(json);
        var info = UpdateService.Parse(doc.RootElement, "0.15.0")!;
        Assert.Equal("https://x/msi", info.MsiUrl);
        Assert.Equal("url:https://x/msi.sha256", info.MsiSha256);
    }

    [Fact] public void ChecksumMismatchIsDetected()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, "hello");
            // SHA-256("hello")
            Assert.True(UpdateService.VerifyHash(f, "2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824"));
            Assert.False(UpdateService.VerifyHash(f, "00"));
        }
        finally { File.Delete(f); }
    }
}
