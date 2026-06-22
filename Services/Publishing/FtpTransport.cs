using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services.Publishing;

/// <summary>FTP transport — delegates to the existing <see cref="IFtpService"/> so behavior is unchanged.</summary>
internal sealed class FtpTransport : IFileTransport
{
    private readonly IFtpService _ftp;
    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _pass;
    private readonly string? _remoteBase;
    private readonly string? _baseUrl;

    public FtpTransport(IFtpService ftp, AppSettings s)
    {
        _ftp = ftp;
        _host = s.FtpHost ?? string.Empty;
        _port = s.FtpPort == 0 ? 21 : s.FtpPort;
        _user = s.FtpUsername ?? string.Empty;
        _pass = s.FtpPassword ?? string.Empty;
        _remoteBase = s.FtpRemotePath;
        _baseUrl = s.BaseDownloadUrl;
    }

    public string DisplayName => "FTP";

    public Task<string> TestAsync(CancellationToken ct = default)
        => _ftp.TestConnectionAsync(_host, _port, _user, _pass, ct);

    public Task UploadFileAsync(string localPath, string remotePath, IProgress<string> progress,
        CancellationToken ct = default, IProgress<long>? bytesProgress = null)
        => _ftp.UploadFilesAsync([(localPath, remotePath)], _host, _port, _user, _pass, progress, ct);

    public Task UploadTextAsync(string content, string remotePath, CancellationToken ct = default)
        => _ftp.UploadStringAsync(content, remotePath, _host, _port, _user, _pass, ct);

    public Task<string?> TryDownloadTextAsync(string remotePath, CancellationToken ct = default)
        => _ftp.TryDownloadStringAsync(remotePath, _host, _port, _user, _pass, ct);

    public Task DeleteFileAsync(string remotePath, CancellationToken ct = default)
        => _ftp.DeleteFilesAsync([remotePath], _host, _port, _user, _pass, new Progress<string>(), ct);

    public Task DeleteDirAsync(string remoteDir, CancellationToken ct = default)
        => _ftp.DeleteDirectoryAsync(remoteDir, _host, _port, _user, _pass, ct);

    public string RemotePath(string appKey, string? version, string fileName)
        => PublishPaths.ServerPath(_remoteBase, appKey, version, fileName);

    public string DownloadUrl(string appKey, string version, string fileName)
        => PublishPaths.DownloadUrl(_baseUrl, appKey, version, fileName);
}
