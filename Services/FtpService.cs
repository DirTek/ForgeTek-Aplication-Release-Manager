using System.IO;
using System.Text;
using FluentFTP;

namespace ForgeTekApplicationReleaseManager.Services;

public class FtpService : IFtpService
{
    private static AsyncFtpClient CreateClient(string host, int port, string username, string password)
    {
        var client = new AsyncFtpClient(host, username, password, port);
        client.Config.ConnectTimeout               = 10_000;
        client.Config.ReadTimeout                  = 15_000;
        client.Config.DataConnectionConnectTimeout = 10_000;
        client.Config.DataConnectionReadTimeout    = 15_000;
        return client;
    }

    public async Task<string> TestConnectionAsync(string host, int port, string username, string password, CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient(host, port, username, password);
            await client.AutoConnect(ct);
            var pwd = await client.GetWorkingDirectory(ct);
            return $"✔  Connected to {host}:{port} (working directory: {pwd})";
        }
        catch (Exception ex)
        {
            return $"✗  {ex.Message}";
        }
    }

    public async Task UploadFilesAsync(
        IEnumerable<(string LocalPath, string RemotePath)> files,
        string host, int port, string username, string password,
        IProgress<string> progress, CancellationToken ct)
    {
        var fileList = files.ToList();
        progress.Report($"[FTP] {fileList.Count} file(s) queued");

        for (var i = 0; i < fileList.Count; i++)
        {
            var (localPath, remotePath) = fileList[i];
            var name = Path.GetFileName(localPath);
            progress.Report($"[FTP] --- file {i + 1}/{fileList.Count}: {name} ---");
            progress.Report($"↑  {name} …");

            // Do NOT use 'using' — Dispose() sends QUIT and waits for 221, which hangs on
            // servers that never ACK the control channel after a data transfer.
            var client = CreateClient(host, port, username, password);
            try
            {
                await client.AutoConnect(ct);
                progress.Report($"Connected (mode: {client.Config.EncryptionMode})");

                // Some shared-hosting FTP servers complete the data transfer but never send
                // the "226 Transfer Complete" ACK on the control channel, hanging indefinitely.
                using var fileCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                fileCts.CancelAfter(TimeSpan.FromSeconds(60));

                // ── Throttled progress ────────────────────────────────────────────
                // Progress<T> marshals every callback to the UI thread via Dispatcher.
                // At ~200+ callbacks/second the dispatcher queue floods and the UI
                // freezes. We throttle to one UI update per 250 ms maximum.
                var transferComplete = false;
                var lastReportTick   = 0L;           // Environment.TickCount64
                var lastPct          = -1.0;

                var ftpProgress = new Progress<FtpProgress>(p =>
                {
                    if (p.Progress >= 100) transferComplete = true;

                    var now = Environment.TickCount64;
                    // Always report the very first tick, every 250 ms after that,
                    // and the final 100% — skip everything else.
                    var isDue   = (now - lastReportTick) >= 250;
                    var isFinal = p.Progress >= 100 && lastPct < 100;
                    if (!isDue && !isFinal) return;

                    lastReportTick = now;
                    lastPct        = p.Progress;

                    var speed = p.TransferSpeed > 0 ? $" @ {p.TransferSpeed / 1024.0:F0} KB/s" : "";
                    progress.Report($"  {p.Progress:F1}%{speed}");
                });

                // Heartbeat: logs every 5 s so the UI never looks completely stuck
                // during the ACK-wait phase (no FTP progress callbacks fire then).
                var uploadTask = client.UploadFile(localPath, remotePath,
                    FtpRemoteExists.Overwrite, createRemoteDir: true,
                    progress: ftpProgress, token: fileCts.Token);

                var heartbeatTask = Task.Run(async () =>
                {
                    while (!fileCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(5_000, fileCts.Token).ConfigureAwait(false);
                        if (fileCts.Token.IsCancellationRequested) break;
                        progress.Report(transferComplete
                            ? "  Waiting for server confirmation…"
                            : "  Uploading…");
                    }
                }, fileCts.Token);

                try
                {
                    var status = await uploadTask;
                    progress.Report(status == FtpStatus.Success
                        ? $"✔  {name} → {remotePath}"
                        : $"⚠  {name} uploaded (server returned: {status})");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested &&
                    (ex is OperationCanceledException || ex is TimeoutException || ex is IOException))
                {
                    // Data was fully sent; server just didn't ACK before timeout.
                    progress.Report($"✔  {name} → {remotePath} (ACK timeout — file transferred)");
                }
                finally
                {
                    fileCts.Cancel();
                    try { await heartbeatTask; } catch { }
                }

                // Size integrity check — fresh connection so upload client state doesn't matter.
                if (!ct.IsCancellationRequested)
                    await VerifyRemoteSizeAsync(localPath, remotePath, host, port, username, password, progress, ct);

                // Disconnect gracefully before dispose to avoid hanging on QUIT ACK.
                using var disconnectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                disconnectCts.CancelAfter(TimeSpan.FromSeconds(5));
                try { await client.Disconnect(disconnectCts.Token); } catch { }
            }
            finally
            {
                client.Dispose();
            }
        }

        progress.Report($"[FTP] all files processed");
    }

    // Connects a dedicated short-lived client and compares the remote file size to the
    // local file. A mismatch after a binary upload is a strong signal of corruption.
    // Reported as a warning rather than an exception — the upload already succeeded and
    // some servers report wrong sizes for ASCII-mode or compressed transfers.
    private async Task VerifyRemoteSizeAsync(
        string localPath, string remotePath,
        string host, int port, string username, string password,
        IProgress<string> progress, CancellationToken ct)
    {
        var localSize = new FileInfo(localPath).Length;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        var client = CreateClient(host, port, username, password);
        try
        {
            await client.AutoConnect(cts.Token);
            var remoteSize = await client.GetFileSize(remotePath, -1L, cts.Token);

            if (remoteSize < 0)
                progress.Report("  —  Server does not support SIZE — integrity check skipped");
            else if (remoteSize == localSize)
                progress.Report($"  ✔  Integrity: {remoteSize:N0} bytes match");
            else
                progress.Report($"  ⚠  Size mismatch — local: {localSize:N0} B, remote: {remoteSize:N0} B");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            progress.Report($"  ⚠  Could not verify upload size: {ex.Message}");
        }
        finally
        {
            using var discCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            discCts.CancelAfter(TimeSpan.FromSeconds(5));
            try { await client.Disconnect(discCts.Token); } catch { }
            client.Dispose();
        }
    }

    /// <summary>
    /// Deletes a list of remote files from the FTP server. Errors per-file are reported
    /// via <paramref name="progress"/> but do not abort the remaining deletions.
    /// </summary>
    public async Task DeleteFilesAsync(
        IEnumerable<string> remotePaths,
        string host, int port, string username, string password,
        IProgress<string> progress, CancellationToken ct = default)
    {
        using var client = CreateClient(host, port, username, password);
        await client.AutoConnect(ct);

        foreach (var remotePath in remotePaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await client.FileExists(remotePath, ct))
                {
                    await client.DeleteFile(remotePath, ct);
                    progress.Report($"✔  Deleted {remotePath}");
                }
                else
                {
                    progress.Report($"—  Not found (already removed): {remotePath}");
                }
            }
            catch (Exception ex)
            {
                progress.Report($"⚠  Could not delete {remotePath}: {ex.Message}");
            }
        }
    }

    public async Task DeleteDirectoryAsync(
        string remotePath,
        string host, int port, string username, string password,
        CancellationToken ct = default)
    {
        var client = CreateClient(host, port, username, password);
        try
        {
            await client.AutoConnect(ct);
            if (await client.DirectoryExists(remotePath, ct))
                await client.DeleteDirectory(remotePath, ct);
        }
        finally
        {
            using var discCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            discCts.CancelAfter(TimeSpan.FromSeconds(5));
            try { await client.Disconnect(discCts.Token); } catch { }
            client.Dispose();
        }
    }

    public async Task UploadStringAsync(
        string content, string remotePath,
        string host, int port, string username, string password,
        CancellationToken ct = default)
    {
        var bytes  = Encoding.UTF8.GetBytes(content);
        var client = CreateClient(host, port, username, password);
        try
        {
            await client.AutoConnect(ct);
            await client.UploadBytes(bytes, remotePath,
                FtpRemoteExists.Overwrite, createRemoteDir: true, token: ct);
        }
        finally
        {
            using var discCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            discCts.CancelAfter(TimeSpan.FromSeconds(5));
            try { await client.Disconnect(discCts.Token); } catch { }
            client.Dispose();
        }
    }

    public async Task<string?> TryDownloadStringAsync(
        string remotePath, string host, int port, string username, string password,
        CancellationToken ct = default)
    {
        // Do NOT use 'using' — Dispose() sends QUIT and waits for 221, which hangs on
        // servers that never ACK the control channel. Disconnect gracefully (bounded by a
        // short CTS) in finally, then dispose, exactly as the upload/delete paths do.
        var client = CreateClient(host, port, username, password);
        try
        {
            await client.AutoConnect(ct);

            if (!await client.FileExists(remotePath, ct))
                return null;

            var bytes = await client.DownloadBytes(remotePath, ct);
            return bytes is { Length: > 0 } ? Encoding.UTF8.GetString(bytes) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            using var discCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            discCts.CancelAfter(TimeSpan.FromSeconds(5));
            try { await client.Disconnect(discCts.Token); } catch { }
            client.Dispose();
        }
    }
}
