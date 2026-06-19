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
