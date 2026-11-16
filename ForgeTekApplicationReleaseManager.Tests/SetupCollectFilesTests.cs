using System.Reflection;
using Xunit;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services;

namespace ForgeTekApplicationReleaseManager.Tests;

public class SetupCollectFilesTests
{
    // v1 shipped a template file, the updater and old.dll. v2's scan no longer has the template file (deleted
    // from the build), excludes the updater from packages (still on disk) and marks old.dll for removal.
    private static AppEntry App() => new()
    {
        Name = "X",
        FolderPath = "C:\\none",
        Versions =
        [
            new AppVersion { Status = VersionStatus.Published, Files =
            [
                new FileRecord { Path = "AboutAssets.txt" }, new FileRecord { Path = "App.Updater.exe" },
                new FileRecord { Path = "app.dll", Checksum = "old" }, new FileRecord { Path = "old.dll" },
            ] },
            new AppVersion { Status = VersionStatus.Published, Files =
            [
                new FileRecord { Path = "App.Updater.exe", IsDebug = true },
                new FileRecord { Path = "app.dll", Checksum = "new" },
                new FileRecord { Path = "old.dll", IsRemoved = true },
            ] },
        ],
    };

    [Fact]
    public void Cumulative_DropsFilesGoneFromLatestScan()
        => Assert.DoesNotContain(Collect(App(), VersionMode.Cumulative), f => f.Path == "AboutAssets.txt");

    [Fact]
    public void Cumulative_KeepsFileOnlyExcludedFromPackages()
        => Assert.Contains(Collect(App(), VersionMode.Cumulative), f => f.Path == "App.Updater.exe");

    [Fact]
    public void Cumulative_DropsFileMarkedForRemoval()
        => Assert.DoesNotContain(Collect(App(), VersionMode.Cumulative), f => f.Path == "old.dll");

    [Fact]
    public void Cumulative_UsesLatestRecord()
        => Assert.Equal("new", Collect(App(), VersionMode.Cumulative).Single(f => f.Path == "app.dll").Checksum);

    [Fact]
    public void LatestOnly_ShipsLatestNonExcludedFiles()
        => Assert.Equal(["app.dll"], Collect(App(), VersionMode.LatestOnly).Select(f => f.Path));

    private static List<FileRecord> Collect(AppEntry entry, VersionMode mode)
    {
        var method = typeof(SetupService).GetMethod("CollectAppFiles", BindingFlags.NonPublic | BindingFlags.Static);
        return (List<FileRecord>)method!.Invoke(null, [entry, mode])!;
    }
}
