using System.Linq;
using Xunit;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services;

namespace ForgeTekApplicationReleaseManager.Tests;

public class ScannerDiffTests
{
    private static FileRecord F(string path, string hash, bool debug = false, bool removed = false)
        => new() { Path = path, Checksum = hash, IsDebug = debug, IsRemoved = removed };

    [Fact]
    public void DiffVersions_ExcludedFile_GoesToExcluded_NotRemoved()
    {
        var baseVersion = new AppVersion
        {
            VersionNumber = "1.0",
            Files = [ F("App.Updater.exe", "U1"), F("app.dll", "A1") ],
        };
        // New scan: updater is now EXCLUDED (on disk, IsDebug), plus the shipped *_new.exe.
        var newFiles = new[]
        {
            F("App.Updater.exe", "U1", debug: true),
            F("App.Updater_new.exe", "U2"),
            F("app.dll", "A1"),
        };

        var diff = new ScannerService().DiffVersions(baseVersion, newFiles);

        Assert.Contains(diff.Excluded, f => f.Path == "App.Updater.exe");
        Assert.DoesNotContain(diff.Removed, f => f.Path == "App.Updater.exe"); // the bug: must NOT be "removed"
        Assert.Contains(diff.Added, f => f.Path == "App.Updater_new.exe");
        Assert.Contains(diff.Unchanged, f => f.Path == "app.dll");
    }

    [Fact]
    public void DiffVersions_UserMarkedRemoval_GoesToRemoved_NotExcluded()
    {
        var baseVersion = new AppVersion
        {
            VersionNumber = "1.0",
            Files = [ F("obsolete.dll", "O1"), F("app.dll", "A1") ],
        };
        // obsolete.dll is still on disk but marked for removal (IsRemoved).
        var newFiles = new[] { F("obsolete.dll", "O1", removed: true), F("app.dll", "A1") };

        var diff = new ScannerService().DiffVersions(baseVersion, newFiles);

        Assert.Contains(diff.Removed, f => f.Path == "obsolete.dll");
        Assert.DoesNotContain(diff.Excluded, f => f.Path == "obsolete.dll");
    }

    [Fact]
    public void DiffVersions_GenuinelyDeletedFile_GoesToRemoved()
    {
        var baseVersion = new AppVersion
        {
            VersionNumber = "1.0",
            Files = [ F("old.dll", "O1"), F("app.dll", "A1") ],
        };
        var newFiles = new[] { F("app.dll", "A1") };   // old.dll is gone from disk entirely

        var diff = new ScannerService().DiffVersions(baseVersion, newFiles);

        Assert.Contains(diff.Removed, f => f.Path == "old.dll");
        Assert.Empty(diff.Excluded);
    }
}
