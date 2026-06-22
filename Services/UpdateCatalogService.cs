using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public class UpdateCatalogService : IUpdateCatalogService
{
    private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

    /// <summary>
    /// Builds or merges a version entry into an update catalog JSON.
    /// If <paramref name="existingJson"/> is provided the new version is merged in;
    /// otherwise a fresh catalog is created.
    /// </summary>
    public string BuildOrMerge(string appKey, AppVersion version, string packageUrl, string? existingJson)
    {
        JsonObject root;
        if (existingJson is not null)
        {
            try { root = JsonNode.Parse(existingJson)!.AsObject(); }
            catch { root = new JsonObject(); }
        }
        else
        {
            root = new JsonObject();
        }

        // Current version pointer
        root[appKey] = version.VersionNumber;

        // Current download URL
        if (root["url"] is not JsonObject urlObj)
        {
            urlObj = new JsonObject();
            root["url"] = urlObj;
        }
        urlObj[appKey] = packageUrl;

        // Incremental version history
        if (root["versions"] is not JsonObject versionsObj)
        {
            versionsObj = new JsonObject();
            root["versions"] = versionsObj;
        }
        versionsObj[version.VersionNumber] = new JsonObject
        {
            ["url"]      = packageUrl,
            ["date"]     = version.ScanDate.ToString("yyyy-MM-dd"),
            ["type"]     = version.PackageType == PackageType.Incremental ? "incremental" : "full",
            // The full baseline this cumulative incremental applies on top of (empty for a full).
            // A client whose installed version is older than this must fetch "latestFull" first.
            ["base"]     = version.BaseVersion ?? string.Empty,
            ["checksum"] = version.PackageChecksum ?? string.Empty,
            ["channel"]  = version.Channel == UpdateChannel.Beta ? "beta" : "stable",
        };

        // The top-level pointer (root[appKey]) tracks the latest STABLE release so existing
        // stable-only clients never receive a beta; betas reach clients that read channels.beta.
        RecomputeChannels(root, appKey, versionsObj);

        return root.ToJsonString(_writeOptions);
    }

    /// <summary>
    /// Rewrites the per-channel pointers (and the legacy top-level pointer) from the version
    /// history. Stable clients follow root[appKey]/channels.stable; beta clients follow
    /// channels.beta, which is the newest version on any channel.
    /// </summary>
    private static void RecomputeChannels(JsonObject root, string appKey, JsonObject versionsObj,
        string? pinnedPointer = null)
    {
        string? stableKey = null, stableUrl = null;
        string? anyKey = null, anyUrl = null;
        string? fullKey = null, fullUrl = null;

        foreach (var kv in versionsObj)
        {
            if (kv.Value is not JsonObject entry) continue;
            var url     = entry["url"]?.GetValue<string>() ?? string.Empty;
            var channel = entry["channel"]?.GetValue<string>() ?? "stable";
            var type    = entry["type"]?.GetValue<string>() ?? "incremental";

            anyKey = kv.Key; anyUrl = url;
            if (!string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase))
            {
                stableKey = kv.Key; stableUrl = url;
            }
            // Newest full baseline — a fresh/old install downloads this before the latest patch.
            if (string.Equals(type, "full", StringComparison.OrdinalIgnoreCase))
            {
                fullKey = kv.Key; fullUrl = url;
            }
        }

        // Top-level pointer: an explicit rollback pin wins; else latest stable, else newest overall.
        var pointerKey = pinnedPointer ?? stableKey ?? anyKey;
        var pointerUrl = pinnedPointer is not null
            ? versionsObj[pinnedPointer]?["url"]?.GetValue<string>() ?? string.Empty
            : (stableKey is not null ? stableUrl : anyUrl);
        if (pointerKey is not null)
        {
            root[appKey] = pointerKey;
            if (root["url"] is not JsonObject urlObj)
            {
                urlObj = new JsonObject();
                root["url"] = urlObj;
            }
            urlObj[appKey] = pointerUrl;
        }

        var channels = new JsonObject();
        if (stableKey is not null)
            channels["stable"] = new JsonObject { ["version"] = stableKey, ["url"] = stableUrl };
        if (anyKey is not null)
            channels["beta"] = new JsonObject { ["version"] = anyKey, ["url"] = anyUrl };
        root["channels"] = channels;

        // The latest full baseline, for fresh installs / installs older than the latest patch's base.
        if (fullKey is not null)
            root["latestFull"] = new JsonObject { ["version"] = fullKey, ["url"] = fullUrl };
        else
            root.Remove("latestFull");
    }

    /// <summary>
    /// Removes a version from the catalog and rolls the current pointer back to
    /// <paramref name="rollbackToVersion"/> when specified, or the last remaining
    /// version otherwise. Returns null if no versions remain (caller should delete
    /// the catalog file rather than uploading an empty one).
    /// </summary>
    public string? RemoveVersion(string appKey, string versionNumber, string existingJson, string? rollbackToVersion = null)
    {
        JsonObject root;
        try { root = JsonNode.Parse(existingJson)!.AsObject(); }
        catch { return null; }

        if (root["versions"] is not JsonObject versionsObj)
            return null;

        versionsObj.Remove(versionNumber);

        if (versionsObj.Count == 0)
            return null;

        // Recompute channel pointers. An explicit rollback target (if it still exists) pins the
        // top-level pointer; otherwise it follows the latest remaining stable.
        var pin = rollbackToVersion is not null && versionsObj[rollbackToVersion] is JsonObject
            ? rollbackToVersion : null;
        RecomputeChannels(root, appKey, versionsObj, pin);

        return root.ToJsonString(_writeOptions);
    }
}
