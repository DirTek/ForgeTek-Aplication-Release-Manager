using System.Text.Json.Nodes;
using Xunit;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.Tests;

public class UpdateCatalogServiceTests
{
    private static readonly UpdateCatalogService Svc = new();

    private static AppVersion Ver(string number, PackageType type, string? baseVersion = null, UpdateChannel channel = UpdateChannel.Stable)
        => new()
        {
            VersionNumber = number,
            PackageType = type,
            BaseVersion = baseVersion,
            Channel = channel,
            ScanDate = new DateTime(2026, 1, 1),
            PackageChecksum = "abc",
        };

    [Fact]
    public void BuildOrMerge_WritesBasePerEntry_AndLatestFullPointer()
    {
        var full = Svc.BuildOrMerge("app", Ver("1.0", PackageType.Full), "https://cdn/app/1.0/app.ftu", null);
        var withInc = Svc.BuildOrMerge("app", Ver("1.2", PackageType.Incremental, baseVersion: "1.0"),
            "https://cdn/app/1.2/app.ftu", full);

        var root = JsonNode.Parse(withInc)!.AsObject();
        var versions = root["versions"]!.AsObject();

        Assert.Equal("", versions["1.0"]!["base"]!.GetValue<string>());     // full has empty base
        Assert.Equal("1.0", versions["1.2"]!["base"]!.GetValue<string>());  // incremental records base

        // latestFull points at the newest full baseline.
        Assert.Equal("1.0", root["latestFull"]!["version"]!.GetValue<string>());
        Assert.Equal("https://cdn/app/1.0/app.ftu", root["latestFull"]!["url"]!.GetValue<string>());

        // Top-level pointer follows the latest stable (the incremental).
        Assert.Equal("1.2", root["app"]!.GetValue<string>());
    }

    [Fact]
    public void BuildOrMerge_LatestFull_AdvancesWhenANewFullIsPublished()
    {
        var json = Svc.BuildOrMerge("app", Ver("1.0", PackageType.Full), "https://cdn/app/1.0/app.ftu", null);
        json = Svc.BuildOrMerge("app", Ver("1.1", PackageType.Incremental, baseVersion: "1.0"), "https://cdn/app/1.1/app.ftu", json);
        json = Svc.BuildOrMerge("app", Ver("2.0", PackageType.Full), "https://cdn/app/2.0/app.ftu", json);   // new baseline
        json = Svc.BuildOrMerge("app", Ver("2.1", PackageType.Incremental, baseVersion: "2.0"), "https://cdn/app/2.1/app.ftu", json);

        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("2.0", root["latestFull"]!["version"]!.GetValue<string>());
        Assert.Equal("2.0", root["versions"]!["2.1"]!["base"]!.GetValue<string>());
    }
}
