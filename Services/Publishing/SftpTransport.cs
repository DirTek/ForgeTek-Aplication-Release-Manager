using System.IO;
using System.Text;
using ForgeTekApplicationReleaseManager.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ForgeTekApplicationReleaseManager.Services.Publishing;

/// <summary>SFTP (SSH file transfer) transport via SSH.NET.</summary>
internal sealed class SftpTransport : IFileTransport
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _pass;
    private readonly string? _remoteBase;
    private readonly string? _baseUrl;

    public SftpTransport(AppSettings s)
    {
        _host = s.SftpHost ?? string.Empty;
        _port = s.SftpPort == 0 ? 22 : s.SftpPort;
        _user = s.SftpUsername ?? string.Empty;
        _pass = s.SftpPassword ?? string.Empty;
        _remoteBase = s.SftpRemotePath;
        _baseUrl = s.SftpBaseDownloadUrl;
    }

    public string DisplayName => "SFTP";

    private SftpClient Connect()
    {
        if (string.IsNullOrWhiteSpace(_host))
            throw new InvalidOperationException("SFTP host is not configured.");
        var client = new SftpClient(_host, _port, _user, _pass);
        client.Connect();
        return client;
    }

    public Task<string> TestAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            using var client = Connect();
            client.ListDirectory(".").GetEnumerator().MoveNext();
            return $"✓ Connected to {_host}:{_port}.";
        }
        catch (Exception ex) { return $"✗ {ex.Message}"; }
    }, ct);

    public Task UploadFileAsync(string localPath, string remotePath, IProgress<string> progress,
        CancellationToken ct = default, IProgress<long>? bytesProgress = null)
        => Task.Run(() =>
        {
            using var client = Connect();
            EnsureRemoteDir(client, PosixDir(remotePath));
            progress.Report($"  ↑ {remotePath}");
            using var fs = File.OpenRead(localPath);
            client.UploadFile(fs, remotePath, canOverride: true,
                uploadCallback: bytesProgress is null ? null : uploaded => bytesProgress.Report((long)uploaded));
        }, ct);

    public Task UploadTextAsync(string content, string remotePath, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var client = Connect();
            EnsureRemoteDir(client, PosixDir(remotePath));
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
            client.UploadFile(ms, remotePath, canOverride: true);
        }, ct);

    public Task<string?> TryDownloadTextAsync(string remotePath, CancellationToken ct = default)
        => Task.Run<string?>(() =>
        {
            try
            {
                using var client = Connect();
                if (!client.Exists(remotePath)) return null;
                using var ms = new MemoryStream();
                client.DownloadFile(remotePath, ms);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
            catch { return null; }
        }, ct);

    public Task DeleteFileAsync(string remotePath, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var client = Connect();
            if (client.Exists(remotePath)) client.DeleteFile(remotePath);
        }, ct);

    public Task DeleteDirAsync(string remoteDir, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var client = Connect();
            DeleteRecursive(client, remoteDir);
        }, ct);

    private static void DeleteRecursive(SftpClient client, string dir)
    {
        if (!client.Exists(dir)) return;
        foreach (var entry in client.ListDirectory(dir))
        {
            if (entry.Name is "." or "..") continue;
            if (entry.IsDirectory) DeleteRecursive(client, entry.FullName);
            else client.DeleteFile(entry.FullName);
        }
        try { client.DeleteDirectory(dir); } catch { }
    }

    // Creates each path segment if missing (no recursive mkdir in SFTP).
    private static void EnsureRemoteDir(SftpClient client, string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || dir == "/") return;
        var segments = dir.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = dir.StartsWith('/') ? "/" : string.Empty;
        foreach (var seg in segments)
        {
            path = path.Length == 0 || path == "/" ? path + seg : path + "/" + seg;
            try { if (!client.Exists(path)) client.CreateDirectory(path); }
            catch (SshException) { /* exists / race — ignore */ }
        }
    }

    private static string PosixDir(string remotePath)
    {
        var idx = remotePath.LastIndexOf('/');
        return idx <= 0 ? "/" : remotePath[..idx];
    }

    public string RemotePath(string appKey, string? version, string fileName)
        => PublishPaths.ServerPath(_remoteBase, appKey, version, fileName);

    public string DownloadUrl(string appKey, string version, string fileName)
        => PublishPaths.DownloadUrl(_baseUrl, appKey, version, fileName);
}
