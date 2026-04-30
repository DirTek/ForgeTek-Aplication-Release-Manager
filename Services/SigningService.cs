using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public enum SignableFileChange { Initial, Added, Modified }

public record SignableFile(string FullPath, string RelativePath, SignableFileChange Change);

public class SigningService
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
        string pfxPath,
        string password,
        IProgress<string> progress,
        CancellationToken ct = default)
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
            for (int i = 0; i < filePaths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var file = filePaths[i];
                progress.Report($"[{i + 1}/{filePaths.Count}] {Path.GetFileName(file)}");

                var psi = new ProcessStartInfo
                {
                    FileName  = signToolPath,
                    Arguments = $"sign /fd SHA256 /sha1 {cert.Thumbprint} \"{file}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                };

                using var proc = Process.Start(psi)!;
                var readOut = proc.StandardOutput.ReadToEndAsync(ct);
                var readErr = proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                var stdout = await readOut;
                var stderr = await readErr;

                if (proc.ExitCode == 0)
                    progress.Report("  ✓ Signed");
                else
                {
                    var msg = (stderr.Length > 0 ? stderr : stdout).Trim();
                    progress.Report($"  ✗ Failed — {(msg.Length > 0 ? msg : $"exit code {proc.ExitCode}")}");
                }
            }
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