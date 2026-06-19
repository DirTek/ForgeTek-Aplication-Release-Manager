using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services.Publishing;

/// <summary>
/// Transport-agnostic publishing facade. Routes upload / catalog / retract operations to the app's
/// configured provider (FTP, SFTP, S3, or GitHub Releases) so the package pipeline is independent
/// of the transport.
/// </summary>
public interface IPublishService
{
    /// <summary>Display name of the app's configured provider (e.g. "SFTP").</summary>
    string ProviderName(AppSettings s);

    /// <summary>True when the configured provider has enough settings to publish.</summary>
    bool IsConfigured(AppSettings s);

    /// <summary>Public URL a client downloads the package from (embedded in the catalog).</summary>
    string ResolveDownloadUrl(AppSettings s, string appKey, string version, string fileName);

    /// <summary>Provider-specific remote location of a file (for display). version = null for catalog.</summary>
    string RemoteTarget(AppSettings s, string appKey, string? version, string fileName);

    Task<string> TestAsync(AppSettings s, CancellationToken ct = default);

    /// <summary>Downloads the current update catalog from the provider, or null if none exists.</summary>
    Task<string?> TryGetCatalogAsync(AppSettings s, string appKey, string catalogFileName, CancellationToken ct = default);

    /// <summary>Uploads the package and the update catalog to the provider.</summary>
    Task UploadReleaseAsync(AppSettings s, string appKey, string version,
        string packageLocalPath, string packageFileName,
        string catalogLocalPath, string catalogFileName,
        IProgress<string> progress, CancellationToken ct = default);

    /// <summary>Removes a published version from the provider and rolls back / deletes the catalog.</summary>
    Task RetractAsync(AppSettings s, AppVersion v, string appKey,
        string packageFileName, string catalogFileName, string? rollbackToVersion,
        IProgress<string> progress, CancellationToken ct = default);
}
