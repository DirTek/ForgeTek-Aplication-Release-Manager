namespace ForgeTekUpdatePackager.Services.Publishing;

/// <summary>
/// A publish target abstracted to a handful of file operations. Each implementation is constructed
/// from an app's settings and knows how to build its own remote paths and public download URLs.
/// "Remote path" is provider-specific: an FTP/SFTP path, an S3 key, or "tag/file" for GitHub Releases.
/// </summary>
public interface IFileTransport
{
    /// <summary>Human-readable provider name for the UI (e.g. "SFTP").</summary>
    string DisplayName { get; }

    Task<string> TestAsync(CancellationToken ct = default);

    Task UploadFileAsync(string localPath, string remotePath, IProgress<string> progress,
        CancellationToken ct = default, IProgress<long>? bytesProgress = null);
    Task UploadTextAsync(string content, string remotePath, CancellationToken ct = default);
    Task<string?> TryDownloadTextAsync(string remotePath, CancellationToken ct = default);
    Task DeleteFileAsync(string remotePath, CancellationToken ct = default);
    /// <summary>Removes a "folder" (FTP/SFTP directory, S3 prefix, or GitHub per-version release).</summary>
    Task DeleteDirAsync(string remoteDir, CancellationToken ct = default);

    /// <summary>Remote location for a file. Pass version = null for catalog-level files.</summary>
    string RemotePath(string appKey, string? version, string fileName);

    /// <summary>Public URL a client downloads the file from.</summary>
    string DownloadUrl(string appKey, string version, string fileName);
}
