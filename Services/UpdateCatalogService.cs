using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public class UpdateCatalogService
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
}
