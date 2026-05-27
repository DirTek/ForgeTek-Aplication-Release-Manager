using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public interface IUpdateCatalogService
{
    string BuildOrMerge(string appKey, AppVersion version, string packageUrl, string? existingJson);
    string? RemoveVersion(string appKey, string versionNumber, string existingJson, string? rollbackToVersion = null);
}
