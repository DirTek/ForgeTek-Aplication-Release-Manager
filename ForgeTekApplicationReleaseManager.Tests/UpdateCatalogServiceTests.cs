using System.Text.Json.Nodes;
using Xunit;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services;

namespace ForgeTekApplicationReleaseManager.Tests;

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

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2, 3]")]
    public void BuildOrMerge_UnreadableExistingCatalog_Throws_InsteadOfReplacingIt(string existing)
        => Assert.Throws<InvalidOperationException>(() =>
            Svc.BuildOrMerge("app", Ver("1.0", PackageType.Full), "https://cdn/app/1.0/app.ftu", existing));

    [Fact]
    public void RemoveVersion_UnreadableCatalog_Throws_InsteadOfDeletingIt()
        => Assert.Throws<InvalidOperationException>(() => Svc.RemoveVersion("app", "1.0", "{ not json"));

    [Fact]
    public void RemoveVersion_CatalogWithoutVersionList_Throws()
        => Assert.Throws<InvalidOperationException>(() => Svc.RemoveVersion("app", "1.0", """{ "app": "1.0" }"""));

    [Fact]
    public void RemoveVersion_LastVersion_ReturnsNull()
    {
        var json = Svc.BuildOrMerge("app", Ver("1.0", PackageType.Full), "https://cdn/app/1.0/app.ftu", null);
        Assert.Null(Svc.RemoveVersion("app", "1.0", json));
    }

    [Fact]
    public void Pointers_FollowVersionNumber_NotPublishOrder()
    {
        // Retract 1.0, publish 2.0, then re-publish 1.0: 1.0 is now the entry added last but must not be "latest".
        var json = Svc.BuildOrMerge("app", Ver("2.0", PackageType.Full), "https://cdn/app/2.0/app.ftu", null);
        json = Svc.BuildOrMerge("app", Ver("1.0", PackageType.Full), "https://cdn/app/1.0/app.ftu", json);

        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("2.0", root["app"]!.GetValue<string>());
        Assert.Equal("2.0", root["channels"]!["stable"]!["version"]!.GetValue<string>());
        Assert.Equal("2.0", root["latestFull"]!["version"]!.GetValue<string>());
        Assert.Equal(["1.0", "2.0"], root["versions"]!.AsObject().Select(kv => kv.Key));   // stored oldest → newest
    }

    [Fact]
    public void SortByVersion_ComparesNumerically_AndLeavesSortedCatalogsUntouched()
    {
        var versions = new JsonObject { ["0.0.7"] = 1, ["0.0.17"] = 2, ["0.0.11"] = 3, ["0.0.12"] = 4 };
        UpdateCatalogService.SortByVersion(versions);
        Assert.Equal(["0.0.7", "0.0.11", "0.0.12", "0.0.17"], versions.Select(kv => kv.Key));   // 17 > 12, not "1" < "7"

        var sorted = new JsonObject { ["0.1.31"] = 1, ["0.1.37"] = 2, ["0.1.60"] = 3 };
        var before = sorted.ToJsonString();
        UpdateCatalogService.SortByVersion(sorted);
        Assert.Equal(before, sorted.ToJsonString());
    }
}
