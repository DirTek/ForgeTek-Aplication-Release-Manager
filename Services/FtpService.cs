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
        // Close by dropping the socket instead of sending QUIT and waiting for the server's 221 ACK.
        // Many shared-hosting servers never ACK after a data transfer, so a polite QUIT hangs and the
        // session lingers server-side — blocking the next operation (e.g. the catalog check) behind a
        // per-user connection limit. Dropping the socket releases the slot immediately.
        client.Config.DisconnectWithQuit = false;
        return client;
    }

    public async Task<string> TestConnectionAsync(string host, int port, string username, string password, CancellationToken ct = default)
    {
        try
        {
            await using var client = CreateClient(host, port, username, password);
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
                // So: once every byte is sent, give the ACK AckTimeout and then move on. Before
                // that, the transfer may take as long as it needs — it only fails if it stalls
                // (no progress for StallTimeout). A timeout is never mistaken for a finished upload.
                using var fileCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var transferComplete = false;
                var stalled          = false;
                var lastActivityTick = Environment.TickCount64;

                // ── Throttled progress ────────────────────────────────────────────
                // Progress<T> marshals every callback to the UI thread via Dispatcher.
                // At ~200+ callbacks/second the dispatcher queue floods and the UI
                // freezes. We throttle to one UI update per 250 ms maximum.
                var lastReportTick   = 0L;           // Environment.TickCount64
                var lastPct          = -1.0;

                var ftpProgress = new Progress<FtpProgress>(p =>
                {
                    Volatile.Write(ref lastActivityTick, Environment.TickCount64);
                    if (p.Progress >= 100 && !transferComplete)
                    {
                        transferComplete = true;
                        try { fileCts.CancelAfter(AckTimeout); } catch (ObjectDisposedException) { }
                    }

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
                        if (!transferComplete &&
                            Environment.TickCount64 - Volatile.Read(ref lastActivityTick) > (long)StallTimeout.TotalMilliseconds)
                        {
                            stalled = true;
                            progress.Report($"  No progress for {StallTimeout.TotalSeconds:F0} s — giving up.");
                            fileCts.Cancel();
                            break;
                        }
                        progress.Report(transferComplete
                            ? "  Waiting for server confirmation…"
                            : "  Uploading…");
                    }
                }, fileCts.Token);

                try
                {
                    var status = await uploadTask;
                    if (status == FtpStatus.Failed)   // not an IOException: the timeout handlers below don't apply
                        throw new InvalidOperationException($"Upload of {name} failed (server returned: {status}).");
                    progress.Report($"✔  {name} → {remotePath}");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && transferComplete && !stalled &&
                    (ex is OperationCanceledException || ex is TimeoutException || ex is IOException))
                {
                    // Every byte was sent; the server just didn't ACK in time. The size check below confirms it.
                    progress.Report($"✔  {name} → {remotePath} (ACK timeout — file transferred)");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested &&
                    (ex is OperationCanceledException || ex is TimeoutException || ex is IOException))
                {
                    // Progress never reached 100% (or stalled). The server may still hold the whole file if its
                    // last progress report fell just short — trust only a matching remote size, never the timeout.
                    var remoteSize = await TryGetRemoteSizeAsync(remotePath, host, port, username, password, ct);
                    if (remoteSize != new FileInfo(localPath).Length)
                        throw new IOException(stalled
                            ? $"Upload of {name} stalled (no progress for {StallTimeout.TotalSeconds:F0} s)."
                            : $"Upload of {name} did not complete.", ex);
                    progress.Report($"✔  {name} → {remotePath} (no server confirmation — remote size matches)");
                }
                finally
                {
                    fileCts.Cancel();
                    try { await heartbeatTask; } catch { }
                }

                // Size integrity check — fresh connection so upload client state doesn't matter. A mismatch
                // aborts the publish, so a truncated package is never followed by a catalog that points at it.
                if (!ct.IsCancellationRequested)
                    await VerifyRemoteSizeAsync(localPath, remotePath, host, port, username, password, progress, ct);

                // Disconnect gracefully before dispose to avoid hanging on QUIT ACK.
                using var disconnectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                disconnectCts.CancelAfter(TimeSpan.FromSeconds(5));
                try { await client.Disconnect(disconnectCts.Token); } catch { }
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        progress.Report($"[FTP] all files processed");
    }

    /// <summary>How long to wait for the server's "transfer complete" reply once every byte has been sent.</summary>
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A transfer with no progress for this long is treated as failed.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    // Remote file size on a fresh connection, or null when it can't be determined.
    private async Task<long?> TryGetRemoteSizeAsync(
        string remotePath, string host, int port, string username, string password, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var client = CreateClient(host, port, username, password);
        try
        {
            await client.AutoConnect(cts.Token);
            var size = await client.GetFileSize(remotePath, -1L, cts.Token);
            return size >= 0 ? size : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            using var discCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            discCts.CancelAfter(TimeSpan.FromSeconds(5));
            try { await client.Disconnect(discCts.Token); } catch { }
            await client.DisposeAsync();
        }
    }

    // Connects a dedicated short-lived client and compares the remote file size to the
    // local file. Uploads are binary, so a mismatch means the remote file is incomplete or
    // corrupt: that throws, stopping the publish before the catalog is uploaded. A server
    // that can't report sizes, or a failed check connection, is only a warning.
    private async Task VerifyRemoteSizeAsync(
        string localPath, string remotePath,
        string host, int port, string username, string password,
        IProgress<string> progress, CancellationToken ct)
    {
        var localSize = new FileInfo(localPath).Length;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        var client = CreateClient(host, port, username, password);
        long remoteSize;
        try
        {
            await client.AutoConnect(cts.Token);
            remoteSize = await client.GetFileSize(remotePath, -1L, cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            progress.Report($"  ⚠  Could not verify upload size: {ex.Message}");
            return;
        }
        finally
        {
            using var discCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            discCts.CancelAfter(TimeSpan.FromSeconds(5));
            try { await client.Disconnect(discCts.Token); } catch { }
            await client.DisposeAsync();
        }

        if (remoteSize < 0)
            progress.Report("  —  Server does not support SIZE — integrity check skipped");
        else if (remoteSize == localSize)
            progress.Report($"  ✔  Integrity: {remoteSize:N0} bytes match");
        else
            throw new IOException(
                $"{Path.GetFileName(localPath)} arrived incomplete — local {localSize:N0} B, remote {remoteSize:N0} B. " +
                "Publish stopped before the catalog was updated; publish again.");
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
        await using var client = CreateClient(host, port, username, password);
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
            await client.DisposeAsync();
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
            var status = await client.UploadBytes(bytes, remotePath,
                FtpRemoteExists.Overwrite, createRemoteDir: true, token: ct);
            if (status == FtpStatus.Failed)
                throw new IOException($"Upload of {remotePath} failed (server returned: {status}).");
        }
        finally
        {
            using var discCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            discCts.CancelAfter(TimeSpan.FromSeconds(5));
            try { await client.Disconnect(discCts.Token); } catch { }
            await client.DisposeAsync();
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
        // No catch-all: a connection or read failure must surface. Callers merge into / roll back the live
        // catalog, and treating "couldn't read it" as "there is none" would replace or orphan it.
        finally
        {
            using var discCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            discCts.CancelAfter(TimeSpan.FromSeconds(5));
            try { await client.Disconnect(discCts.Token); } catch { }
            await client.DisposeAsync();
        }
    }
}
