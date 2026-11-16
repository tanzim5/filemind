using FileMind.Core;
using Xunit;

namespace FileMind.Core.Tests;

public class SpaceDescriptionTests
{
    [Fact] public void CleanTrimsDropsEmptyAndDuplicatesAndCaps()
    {
        Assert.Equal(["cats", "dogs at the beach"], SpaceDescriptions.Clean([" cats ", "", "Cats", "dogs at the beach", "  "]));
        Assert.Equal(SpaceDescriptions.MaxDescriptions, SpaceDescriptions.Clean(Enumerable.Range(0, 20).Select(i => "d" + i)).Count);
    }

    [Fact] public void NameDefaultsToTheFirstDescriptionCapitalised()
    {
        Assert.Equal("Cats", SpaceDescriptions.NameFor("", ["cats", "dogs"]));
        Assert.Equal("Dogs at the beach", SpaceDescriptions.NameFor(null, ["dogs at the beach"]));
        Assert.Equal("Pets", SpaceDescriptions.NameFor("  Pets ", ["cats"]));
        Assert.Equal("", SpaceDescriptions.NameFor("", []));
    }
}

public class DismissedSpacesTests
{
    private static ClusterProposal P(string n) => new() { Name = n, Count = 9 };

    [Fact] public void DismissedProposalsStayHiddenAcrossSessions()
    {
        var file = Path.Combine(Path.GetTempPath(), "fm-dismissed-" + Guid.NewGuid() + ".json");
        try
        {
            var a = new DismissedSpaces(file);
            a.Dismiss("Midjourney");
            Assert.True(new DismissedSpaces(file).IsDismissed("  midjourney "));
            var visible = new DismissedSpaces(file).Visible([P("Midjourney"), P("Videos"), P("Portraits")]);
            Assert.Equal(["Videos", "Portraits"], visible.Select(p => p.Name));
        }
        finally { File.Delete(file); }
    }

    [Fact] public void VisibleIsCappedAndAMissingOrBrokenFileIsFine()
    {
        var file = Path.Combine(Path.GetTempPath(), "fm-dismissed-" + Guid.NewGuid() + ".json");
        File.WriteAllText(file, "not json");
        try { Assert.Equal(6, new DismissedSpaces(file).Visible(Enumerable.Range(0, 10).Select(i => P("g" + i))).Count); }
        finally { File.Delete(file); }
        Assert.Empty(new DismissedSpaces(file).Visible([]));
    }
}
