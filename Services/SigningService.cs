using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services;

public enum SignableFileChange { Initial, Added, Modified }

public record SignableFile(string FullPath, string RelativePath, SignableFileChange Change);

public class SigningService : ISigningService
{
    private static readonly HashSet<string> SignableExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".sys", ".ocx", ".msi", ".cab", ".cat" };

    public string? FindSignTool()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir.Trim(), "signtool.exe");
            if (File.Exists(candidate)) return candidate;
        }

        var kitsRoot = @"C:\Program Files (x86)\Windows Kits\10\bin";
        if (Directory.Exists(kitsRoot))
        {
            return Directory.GetDirectories(kitsRoot, "10.*")
                .OrderByDescending(d => d)
                .Select(d => Path.Combine(d, "x64", "signtool.exe"))
                .FirstOrDefault(File.Exists);
        }

        return null;
    }

    public IReadOnlyList<SignableFile> GetSignableFiles(
        IEnumerable<FileRecord> files, string rootPath, AppVersion? baseVersion = null)
    {
        var candidates = files
            .Where(f => !f.IsDebug && SignableExtensions.Contains(Path.GetExtension(f.Path)));

        if (baseVersion is null)
        {
            return candidates
                .Select(f => new SignableFile(Path.Combine(rootPath, f.Path), f.Path, SignableFileChange.Initial))
                .Where(sf => File.Exists(sf.FullPath))
                .ToList();
        }

        var baseMap = baseVersion.NonDebugFiles
            .ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        return candidates
            .Where(f => !baseMap.TryGetValue(f.Path, out var prev) || prev.Checksum != f.Checksum)
            .Select(f =>
            {
                var change = baseMap.ContainsKey(f.Path) ? SignableFileChange.Modified : SignableFileChange.Added;
                return new SignableFile(Path.Combine(rootPath, f.Path), f.Path, change);
            })
            .Where(sf => File.Exists(sf.FullPath))
            .ToList();
    }

    public async Task SignFilesAsync(
        string signToolPath,
        IReadOnlyList<string> filePaths,
        string? pfxPath,
        string? password,
        string? storeThumbprint,
        IProgress<string> progress,
        CancellationToken ct = default)
    {
        X509Certificate2? storeCert = null;

        if (!string.IsNullOrWhiteSpace(storeThumbprint))
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            var certs = store.Certificates.Find(X509FindType.FindByThumbprint, storeThumbprint, validOnly: false);
            storeCert = certs.Count > 0 ? certs[0] : null;
            store.Close();

            if (storeCert is null)
            {
                progress.Report("  ✗ Certificate with the specified thumbprint not found in Windows Store");
                return;
            }
        }

        if (storeCert is not null)
        {
            try
            {
                await SignWithCertAsync(signToolPath, filePaths, storeCert, progress, ct);
            }
            finally
            {
                storeCert.Dispose();
            }
        }
        else if (!string.IsNullOrWhiteSpace(pfxPath))
        {
            await SignWithPfxAsync(signToolPath, filePaths, pfxPath, password ?? string.Empty, progress, ct);
        }
        else
        {
            progress.Report("  ✗ No certificate specified — provide a PFX path or select a Windows Store certificate");
        }
    }

    // RFC3161 timestamp authority — without a timestamp, signatures stop validating once the
    // signing certificate expires.
    private const string TimestampUrl = "http://timestamp.digicert.com";

    private async Task SignWithCertAsync(
        string signToolPath,
        IReadOnlyList<string> filePaths,
        X509Certificate2 cert,
        IProgress<string> progress,
        CancellationToken ct)
    {
        for (int i = 0; i < filePaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var file = filePaths[i];
            progress.Report($"[{i + 1}/{filePaths.Count}] {Path.GetFileName(file)}");

            // Prefer a timestamped signature; fall back to untimestamped if the TSA can't be reached
            // (e.g. offline) so signing still succeeds rather than failing outright.
            var thumb = cert.Thumbprint;
            var (code, msg) = await RunSignToolAsync(signToolPath,
                $"sign /fd SHA256 /tr {TimestampUrl} /td SHA256 /sha1 {thumb} \"{file}\"", ct);

            if (code == 0)
            {
                progress.Report("  ✓ Signed (timestamped)");
                continue;
            }

            var (code2, msg2) = await RunSignToolAsync(signToolPath,
                $"sign /fd SHA256 /sha1 {thumb} \"{file}\"", ct);

            if (code2 == 0)
                progress.Report("  ⚠ Signed without timestamp — could not reach the timestamp server.");
            else
            {
                var detail = (msg2.Length > 0 ? msg2 : msg).Trim();
                progress.Report($"  ✗ Failed — {(detail.Length > 0 ? detail : $"exit code {code2}")}");
            }
        }
    }

    private static async Task<(int ExitCode, string Output)> RunSignToolAsync(
        string signToolPath, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName  = signToolPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute = false,
            CreateNoWindow  = true,
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start signtool.exe.");
        var readOut = proc.StandardOutput.ReadToEndAsync(ct);
        var readErr = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await readOut;
        var stderr = await readErr;
        return (proc.ExitCode, stderr.Length > 0 ? stderr : stdout);
    }

    private async Task SignWithPfxAsync(
        string signToolPath,
        IReadOnlyList<string> filePaths,
        string pfxPath,
        string password,
        IProgress<string> progress,
        CancellationToken ct)
    {
        // Import the PFX into the CurrentUser store for the duration of signing so
        // signtool can be invoked with /sha1 <thumbprint> — the password never
        // appears in the process command line and is therefore not visible in
        // process listings or audit logs.
        var certBytes = await File.ReadAllBytesAsync(pfxPath, ct);
        using var cert = X509CertificateLoader.LoadPkcs12(certBytes, password,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cert);

        try
        {
            await SignWithCertAsync(signToolPath, filePaths, cert, progress, ct);
        }
        finally
        {
            store.Remove(cert);
            store.Close();

            // Best-effort: delete the transient CNG key container to avoid
            // leaving an orphaned private key on disk after the cert is removed.
            try
            {
                using var rsa = cert.GetRSAPrivateKey();
                if (rsa is RSACng cng) cng.Key.Delete();
            }
            catch { }
        }
    }
}