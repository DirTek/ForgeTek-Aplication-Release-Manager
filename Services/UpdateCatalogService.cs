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
            ["checksum"] = version.PackageChecksum ?? string.Empty,
        };

        return root.ToJsonString(_writeOptions);
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

        // Prefer the explicitly supplied rollback target; fall back to insertion-order last.
        var remainingKeys = versionsObj.Select(kv => kv.Key).ToList();
        var latestKey = rollbackToVersion is not null && remainingKeys.Contains(rollbackToVersion)
            ? rollbackToVersion
            : remainingKeys.Last();
        var latestEntry = versionsObj[latestKey]!.AsObject();
        var latestUrl   = latestEntry["url"]?.GetValue<string>() ?? string.Empty;

        root[appKey] = latestKey;

        if (root["url"] is not JsonObject urlObj)
        {
            urlObj = new JsonObject();
            root["url"] = urlObj;
        }
        urlObj[appKey] = latestUrl;

        return root.ToJsonString(_writeOptions);
    }
}
