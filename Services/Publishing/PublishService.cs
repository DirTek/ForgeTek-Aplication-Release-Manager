using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services.Publishing;

public class PublishService : IPublishService
{
    private readonly IFtpService _ftp;
    private readonly ISettingsService _settings;
    private readonly IUpdateCatalogService _catalog;

    public PublishService(IFtpService ftp, ISettingsService settings, IUpdateCatalogService catalog)
    {
        _ftp = ftp;
        _settings = settings;
        _catalog = catalog;
    }

    // Normalizes the provider string; defaults to FTP for legacy apps with no provider set.
    private static string Normalize(string? provider) => provider switch
    {
        "Sftp" => "Sftp",
        "S3" => "S3",
        "GitHubReleases" => "GitHubReleases",
        _ => "Ftp",
    };

    private string EffectiveGitHubToken(AppSettings s)
        => string.IsNullOrWhiteSpace(s.GitHubToken) ? (_settings.Global.GitHubToken ?? string.Empty) : s.GitHubToken!;

    private IFileTransport CreateTransport(AppSettings s, string? providerOverride = null)
        => Normalize(providerOverride ?? s.PublishProvider) switch
        {
            "Sftp" => new SftpTransport(s),
            "S3" => new S3Transport(s),
            "GitHubReleases" => new GitHubReleaseTransport(
                s.GitHubRepo ?? string.Empty, EffectiveGitHubToken(s), s.GitHubReleaseTag, s.GitHubCatalogTag),
            _ => new FtpTransport(_ftp, s),
        };

    public string ProviderName(AppSettings s) => CreateTransport(s).DisplayName;

    public bool IsConfigured(AppSettings s) => Normalize(s.PublishProvider) switch
    {
        "Sftp" => !string.IsNullOrWhiteSpace(s.SftpHost),
        "S3" => !string.IsNullOrWhiteSpace(s.S3Bucket)
                && !string.IsNullOrWhiteSpace(s.S3AccessKey)
                && !string.IsNullOrWhiteSpace(s.S3SecretKey),
        "GitHubReleases" => !string.IsNullOrWhiteSpace(s.GitHubRepo)
                && !string.IsNullOrWhiteSpace(EffectiveGitHubToken(s)),
        _ => !string.IsNullOrWhiteSpace(s.FtpHost),
    };

    public string ResolveDownloadUrl(AppSettings s, string appKey, string version, string fileName)
        => CreateTransport(s).DownloadUrl(appKey, version, fileName);

    public string RemoteTarget(AppSettings s, string appKey, string? version, string fileName)
        => CreateTransport(s).RemotePath(appKey, version, fileName);

    public Task<string> TestAsync(AppSettings s, CancellationToken ct = default)
        => CreateTransport(s).TestAsync(ct);

    public Task<string?> TryGetCatalogAsync(AppSettings s, string appKey, string catalogFileName, CancellationToken ct = default)
    {
        var t = CreateTransport(s);
        return t.TryDownloadTextAsync(t.RemotePath(appKey, null, catalogFileName), ct);
    }

    public async Task UploadReleaseAsync(AppSettings s, string appKey, string version,
        string packageLocalPath, string packageFileName,
        string catalogLocalPath, string catalogFileName,
        IProgress<string> progress, CancellationToken ct = default)
    {
        var t = CreateTransport(s);
        await t.UploadFileAsync(packageLocalPath, t.RemotePath(appKey, version, packageFileName), progress, ct);
        await t.UploadFileAsync(catalogLocalPath, t.RemotePath(appKey, null, catalogFileName), progress, ct);
    }

    public async Task<string> UploadArtifactAsync(AppSettings s, string appKey, string version,
        string localPath, string fileName, IProgress<string> progress, CancellationToken ct = default,
        IProgress<long>? bytesProgress = null)
    {
        var t = CreateTransport(s);
        await t.UploadFileAsync(localPath, t.RemotePath(appKey, version, fileName), progress, ct, bytesProgress);
        return t.DownloadUrl(appKey, version, fileName);
    }

    public async Task DeleteArtifactAsync(AppSettings s, string appKey, string version,
        string fileName, IProgress<string> progress, CancellationToken ct = default)
    {
        var t = CreateTransport(s);
        var remote = t.RemotePath(appKey, version, fileName);
        await t.DeleteFileAsync(remote, ct);
        progress.Report($"Removed {remote}");
    }

    public async Task RetractAsync(AppSettings s, AppVersion v, string appKey,
        string packageFileName, string catalogFileName, string? rollbackToVersion,
        IProgress<string> progress, CancellationToken ct = default)
    {
        var t = CreateTransport(s, v.PublishProvider);
        var catalogRemote = t.RemotePath(appKey, null, catalogFileName);

        // Roll the catalog back (or delete it when no versions remain).
        var existing = await t.TryDownloadTextAsync(catalogRemote, ct);
        if (existing is not null)
        {
            var updated = _catalog.RemoveVersion(appKey, v.VersionNumber, existing, rollbackToVersion);
            if (updated is not null)
            {
                await t.UploadTextAsync(updated, catalogRemote, ct);
                progress.Report($"Catalog rolled back: {catalogRemote}");
            }
            else
            {
                await t.DeleteFileAsync(catalogRemote, ct);
                progress.Report($"Catalog deleted (no versions remain): {catalogRemote}");
            }
        }

        // Delete the package, then its version "folder".
        var packageRemote = t.RemotePath(appKey, v.VersionNumber, packageFileName);
        await t.DeleteFileAsync(packageRemote, ct);
        progress.Report($"Package deleted: {packageRemote}");

        var lastSlash = packageRemote.LastIndexOf('/');
        if (lastSlash > 0)
        {
            var versionFolder = packageRemote[..lastSlash];
            try
            {
                await t.DeleteDirAsync(versionFolder, ct);
                progress.Report($"Version folder removed: {versionFolder}");
            }
            catch (Exception ex) { progress.Report($"Folder cleanup skipped: {ex.Message}"); }
        }
    }
}
