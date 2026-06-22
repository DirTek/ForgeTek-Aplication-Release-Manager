using System.Collections.Generic;
using System.Linq;
using Xunit;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services;

namespace ForgeTekApplicationReleaseManager.Tests;

public class SourceCompareTests
{
    private static Dictionary<string, string> Map(params (string Path, string Hash)[] entries)
        => entries.ToDictionary(e => e.Path, e => e.Hash, System.StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Compare_AllIdentical_WhenSameHashesEverywhere()
    {
        var a = Map(("app.dll", "AAA"), ("app.exe", "BBB"));
        var report = SourceCompareService.Compare(a, Map(("app.dll", "AAA"), ("app.exe", "BBB")),
                                                     Map(("app.dll", "AAA"), ("app.exe", "BBB")));
        Assert.True(report.AllIdentical);
        Assert.Equal(2, report.Identical);
        Assert.Equal(0, report.Differs);
        Assert.Equal(0, report.Partial);
        Assert.True(report.HasFiles && report.HasSln && report.HasGitHub);
    }

    [Fact]
    public void Compare_FlagsDiffersAndMissing()
    {
        var files = Map(("app.dll", "AAA"), ("app.exe", "BBB"), ("extra.txt", "CCC"));
        var sln = Map(("app.dll", "AAA"), ("app.exe", "ZZZ"));   // app.exe differs; extra.txt missing
        var report = SourceCompareService.Compare(files, sln, github: null);

        Assert.False(report.AllIdentical);
        Assert.Equal(1, report.Identical);  // app.dll
        Assert.Equal(1, report.Differs);    // app.exe
        Assert.Equal(1, report.Partial);    // extra.txt missing in sln

        var exe = report.Rows.Single(r => r.Path == "app.exe");
        Assert.Equal(CompareStatus.Differs, exe.Status);
        Assert.Equal("BBB", exe.FilesHash);
        Assert.Equal("ZZZ", exe.SlnHash);
        Assert.Null(exe.GitHubHash);   // github wasn't provided

        var extra = report.Rows.Single(r => r.Path == "extra.txt");
        Assert.Equal(CompareStatus.Partial, extra.Status);
    }

    [Fact]
    public void Compare_OnlyActiveSourcesCount_TowardIdentical()
    {
        // Only files + github provided (sln null). A file present in both with equal hash is Identical.
        var report = SourceCompareService.Compare(
            Map(("a.dll", "H1")), sln: null, github: Map(("a.dll", "H1")));
        Assert.True(report.AllIdentical);
        Assert.False(report.HasSln);
        Assert.Single(report.Rows);
    }

    [Fact]
    public void Compare_CaseInsensitivePaths()
    {
        var report = SourceCompareService.Compare(
            Map(("App.dll", "H1")), Map(("app.dll", "H1")), github: null);
        Assert.Single(report.Rows);
        Assert.Equal(CompareStatus.Identical, report.Rows[0].Status);
    }
}
