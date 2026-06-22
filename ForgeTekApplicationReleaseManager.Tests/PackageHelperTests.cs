using Xunit;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;
using ForgeTekUpdatePackager.Services.Publishing;
using ForgeTekUpdatePackager.ViewModels;

namespace ForgeTekUpdatePackager.Tests;

public class PackageHelperTests
{
    [Theory]
    [InlineData(null, "myapp", null, "myapp.json", "/myapp/myapp.json")]
    [InlineData("/base", "myapp", "1.0", "pkg.ftu", "/base/myapp/1.0/pkg.ftu")]
    [InlineData("ftp://server.com/path", "myapp", null, "file.json", "/path/myapp/file.json")]
    [InlineData("ftps://secure.com/root/", "app", "2.0", "app.zip", "/root/app/2.0/app.zip")]
    public void BuildRemotePath_ReturnsCorrectPath(string? basePath, string appKey, string? version, string filename, string expected)
    {
        var result = InvokeBuildRemotePath(basePath, appKey, version, filename);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, "myapp", "1.0", "pkg.ftu", "myapp/1.0/pkg.ftu")]
    [InlineData("https://updates.example.com", "myapp", "1.0", "pkg.ftu", "https://updates.example.com/myapp/1.0/pkg.ftu")]
    [InlineData("ftp://files.example.com/pub", "myapp", "1.0", "pkg.ftu", "https://files.example.com/pub/myapp/1.0/pkg.ftu")]
    public void BuildDownloadUrl_ReturnsCorrectUrl(string? baseUrl, string appKey, string version, string filename, string expected)
    {
        var result = InvokeBuildDownloadUrl(baseUrl, appKey, version, filename);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ComputeIncrementalFiles_WithNoBaseVersion_ReturnsAllNonDebug()
    {
        var version = new AppVersion
        {
            VersionNumber = "1.0",
            Files =
            [
                new FileRecord { Path = "file1.dll", Checksum = "A", IsDebug = false },
                new FileRecord { Path = "file2.exe", Checksum = "B", IsDebug = false },
            ]
        };

        var result = InvokeComputeIncrementalFiles(version, null);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ComputeIncrementalFiles_WithBaseVersion_FiltersUnchanged()
    {
        var version = new AppVersion
        {
            VersionNumber = "2.0",
            Files =
            [
                new FileRecord { Path = "file1.dll", Checksum = "A", IsDebug = false },
                new FileRecord { Path = "file2.exe", Checksum = "C", IsDebug = false },
                new FileRecord { Path = "file3.dll", Checksum = "D", IsDebug = false },
            ]
        };
        var baseVersion = new AppVersion
        {
            VersionNumber = "1.0",
            Files =
            [
                new FileRecord { Path = "file1.dll", Checksum = "A", IsDebug = false },
                new FileRecord { Path = "file2.exe", Checksum = "B", IsDebug = false },
            ]
        };

        var result = InvokeComputeIncrementalFiles(version, baseVersion);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, f => f.Path == "file2.exe");
        Assert.Contains(result, f => f.Path == "file3.dll");
    }

    [Fact]
    public void SelectBaselineFull_PicksMostRecentFull()
    {
        var v1 = new AppVersion { VersionNumber = "1.0", IsInitial = true, PackageType = PackageType.Full };
        var v2 = new AppVersion { VersionNumber = "2.0", PackageType = PackageType.Incremental };
        var v3 = new AppVersion { VersionNumber = "3.0", PackageType = PackageType.Incremental };
        var v4 = new AppVersion { VersionNumber = "4.0", PackageType = PackageType.Full };   // new baseline
        var v5 = new AppVersion { VersionNumber = "5.0", PackageType = PackageType.Incremental };
        var versions = new List<AppVersion> { v1, v2, v3, v4, v5 };

        Assert.Null(InvokeSelectBaselineFull(versions, 0));            // initial has no baseline
        Assert.Equal("1.0", InvokeSelectBaselineFull(versions, 2)!.VersionNumber); // v3 → v1
        Assert.Equal("4.0", InvokeSelectBaselineFull(versions, 4)!.VersionNumber); // v5 → v4
    }

    [Fact]
    public void CumulativeIncremental_IncludesFileAddedInSkippedIntermediateVersion()
    {
        // v1 baseline; helper.dll ADDED in v2; app.exe MODIFIED in v3. The bug: a v1 user applying only
        // v3 misses helper.dll. With cumulative-from-baseline, v3's payload (vs v1) must include it.
        var v1 = new AppVersion { VersionNumber = "1.0", IsInitial = true, PackageType = PackageType.Full,
            Files = [ new FileRecord { Path = "app.exe", Checksum = "A1" } ] };
        var v2 = new AppVersion { VersionNumber = "2.0", PackageType = PackageType.Incremental,
            Files = [ new FileRecord { Path = "app.exe", Checksum = "A1" }, new FileRecord { Path = "helper.dll", Checksum = "H1" } ] };
        var v3 = new AppVersion { VersionNumber = "3.0", PackageType = PackageType.Incremental,
            Files = [ new FileRecord { Path = "app.exe", Checksum = "A2" }, new FileRecord { Path = "helper.dll", Checksum = "H1" } ] };
        var versions = new List<AppVersion> { v1, v2, v3 };

        var baseline = InvokeSelectBaselineFull(versions, 2);
        Assert.Equal("1.0", baseline!.VersionNumber);

        var payload = InvokeComputeIncrementalFiles(v3, baseline);
        Assert.Contains(payload, f => f.Path == "helper.dll");   // the regression this fix targets
        Assert.Contains(payload, f => f.Path == "app.exe");      // modified vs baseline
    }

    [Fact]
    public void ComputeRemovedFiles_IsBaselineRelative()
    {
        var baseline = new AppVersion { VersionNumber = "1.0",
            Files = [ new FileRecord { Path = "keep.dll", Checksum = "K" }, new FileRecord { Path = "gone.dll", Checksum = "G" } ] };
        var version = new AppVersion { VersionNumber = "3.0",
            Files = [ new FileRecord { Path = "keep.dll", Checksum = "K" } ] };

        var removed = InvokeComputeRemovedFiles(version, baseline);
        Assert.Single(removed);
        Assert.Contains("gone.dll", removed);
    }

    [Fact]
    public void ComputeRemovedFiles_ExcludedFileIsNotFlaggedForDeletion()
    {
        // The live self-updater: shipped in the baseline, EXCLUDED now (kept on disk as debug). It must
        // NOT appear in RemovedFiles (clients must not delete the running updater).
        var baseline = new AppVersion { VersionNumber = "1.0",
            Files = [ new FileRecord { Path = "App.Updater.exe", Checksum = "U1", IsDebug = false } ] };
        var version = new AppVersion { VersionNumber = "2.0",
            Files =
            [
                new FileRecord { Path = "App.Updater.exe", Checksum = "U1", IsDebug = true },   // excluded, on disk
                new FileRecord { Path = "App.Updater_new.exe", Checksum = "U2", IsDebug = false }, // the shipped swap
            ] };

        var removed = InvokeComputeRemovedFiles(version, baseline);
        Assert.Empty(removed);   // excluded ≠ removed
    }

    [Fact]
    public void ComputeRemovedFiles_ExplicitRemovalIsIncluded()
    {
        // The user marks a file for deletion on clients (IsRemoved) — it must land in RemovedFiles.
        var baseline = new AppVersion { VersionNumber = "1.0",
            Files = [ new FileRecord { Path = "obsolete.dll", Checksum = "O", IsDebug = false } ] };
        var version = new AppVersion { VersionNumber = "2.0",
            Files = [ new FileRecord { Path = "obsolete.dll", Checksum = "O", IsRemoved = true } ] };

        var removed = InvokeComputeRemovedFiles(version, baseline);
        Assert.Contains("obsolete.dll", removed);
    }

    private static AppVersion? InvokeSelectBaselineFull(IReadOnlyList<AppVersion> versions, int idx)
    {
        var method = typeof(PackageViewModel).GetMethod("SelectBaselineFull",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (AppVersion?)method!.Invoke(null, [versions, idx]);
    }

    private static IReadOnlyList<string> InvokeComputeRemovedFiles(AppVersion version, AppVersion? baseVersion)
    {
        var method = typeof(PackageViewModel).GetMethod("ComputeRemovedFiles",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (IReadOnlyList<string>)method!.Invoke(null, [version, baseVersion])!;
    }

    // Path/URL construction now lives in the publishing layer's shared helper.
    private static string InvokeBuildRemotePath(string? basePath, string appKey, string? version, string filename)
        => PublishPaths.ServerPath(basePath, appKey, version, filename);

    private static string InvokeBuildDownloadUrl(string? baseUrl, string appKey, string version, string filename)
        => PublishPaths.DownloadUrl(baseUrl, appKey, version, filename);

    private static IReadOnlyList<FileRecord> InvokeComputeIncrementalFiles(AppVersion version, AppVersion? baseVersion)
    {
        var method = typeof(PackageViewModel).GetMethod("ComputeIncrementalFiles",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (IReadOnlyList<FileRecord>)method!.Invoke(null, [version, baseVersion])!;
    }
}
