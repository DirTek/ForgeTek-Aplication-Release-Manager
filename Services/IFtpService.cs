namespace ForgeTekApplicationReleaseManager.Services;

public interface IFtpService
{
    Task<string> TestConnectionAsync(string host, int port, string username, string password, CancellationToken ct = default);
    Task UploadFilesAsync(IEnumerable<(string LocalPath, string RemotePath)> files, string host, int port, string username, string password, IProgress<string> progress, CancellationToken ct);
    Task DeleteFilesAsync(IEnumerable<string> remotePaths, string host, int port, string username, string password, IProgress<string> progress, CancellationToken ct = default);
    Task DeleteDirectoryAsync(string remotePath, string host, int port, string username, string password, CancellationToken ct = default);
    Task UploadStringAsync(string content, string remotePath, string host, int port, string username, string password, CancellationToken ct = default);
    /// <summary>Downloads a text file, or returns null when it doesn't exist (or is empty). Throws when the
    /// server can't be reached or the file can't be read — never reported as "missing".</summary>
    Task<string?> TryDownloadStringAsync(string remotePath, string host, int port, string username, string password, CancellationToken ct = default);
}
