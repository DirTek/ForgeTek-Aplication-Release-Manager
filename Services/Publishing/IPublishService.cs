using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services.Publishing;

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

    /// <summary>Uploads a single arbitrary file (e.g. a generated setup .exe) to the version's location
    /// and returns its public download URL. Does not touch the update catalog. <paramref name="bytesProgress"/>
    /// reports cumulative bytes uploaded (where the provider supports it) for a progress bar.</summary>
    Task<string> UploadArtifactAsync(AppSettings s, string appKey, string version,
        string localPath, string fileName,
        IProgress<string> progress, CancellationToken ct = default, IProgress<long>? bytesProgress = null);

    /// <summary>Removes a previously published artifact (e.g. a setup .exe) from the provider.</summary>
    Task DeleteArtifactAsync(AppSettings s, string appKey, string version,
        string fileName, IProgress<string> progress, CancellationToken ct = default);

    /// <summary>Removes a published version from the provider and rolls back / deletes the catalog.</summary>
    Task RetractAsync(AppSettings s, AppVersion v, string appKey,
        string packageFileName, string catalogFileName, string? rollbackToVersion,
        IProgress<string> progress, CancellationToken ct = default);
}
