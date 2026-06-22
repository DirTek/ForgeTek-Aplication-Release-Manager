using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ForgeTekUpdatePackager.Services;

public class CertificateService : ICertificateService
{
    public string ReadThumbprint(byte[] pfx, string password)
    {
        using var cert = X509CertificateLoader.LoadPkcs12(pfx, password);
        return cert.Thumbprint;
    }

    public void ImportToUserStore(byte[] pfx, string password)
    {
        // PersistKeySet keeps the private key in the user's key store; Exportable lets it be re-exported later.
        using var cert = X509CertificateLoader.LoadPkcs12(pfx, password,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cert);
        store.Close();
    }

    /// <summary>
    /// Generates a self-signed code-signing certificate using PowerShell's
    /// New-SelfSignedCertificate, exports it as a PFX, then removes it from
    /// the Windows certificate store so no permanent store entry is left behind.
    /// </summary>
    /// <returns>Full path to the generated .pfx file.</returns>
    public async Task<string> GenerateSelfSignedCertAsync(
        string subject, string friendlyName, string password, string outputFolder,
        bool keepInStore,
        IProgress<string> progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("Subject (CN) is required.", nameof(subject));
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("A password is required to protect the PFX.", nameof(password));

        Directory.CreateDirectory(outputFolder);

        var safeName   = string.Concat(subject.Replace(" ", "_").Split(Path.GetInvalidFileNameChars()));
        var outputPath = Path.Combine(outputFolder, $"{safeName}.pfx");

        var script    = BuildScript(subject, friendlyName, password, outputPath, keepInStore);
        var tempScript = Path.ChangeExtension(Path.GetTempFileName(), ".ps1");
        try
        {
            await File.WriteAllTextAsync(tempScript, script, Encoding.UTF8, ct);

            var psi = new ProcessStartInfo
            {
                FileName               = "powershell.exe",
                Arguments              = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScript}\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            progress.Report("Launching PowerShell…");
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start PowerShell.");

            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            foreach (var line in (await outTask).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!string.IsNullOrWhiteSpace(line)) progress.Report(line);

            var stderr = await errTask;
            if (!string.IsNullOrWhiteSpace(stderr))
                foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!string.IsNullOrWhiteSpace(line)) progress.Report($"⚠  {line}");

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"PowerShell exited with code {proc.ExitCode}.");

            if (!File.Exists(outputPath))
                throw new FileNotFoundException("Certificate was not created at expected path.", outputPath);

            return outputPath;
        }
        finally
        {
            try { File.Delete(tempScript); } catch { }
        }
    }

    private static string BuildScript(string subject, string friendlyName, string password, string outputPath, bool keepInStore)
    {
        var safeSubject      = Ps(subject);
        var safeFriendly     = Ps(string.IsNullOrWhiteSpace(friendlyName) ? subject : friendlyName);
        var safePassword     = Ps(password);
        var safeOutputPath   = Ps(outputPath);

        var removeStep = keepInStore
            ? "$null  # keep in store — no removal"
            : $"Remove-Item -Path \"Cert:\\CurrentUser\\My\\$($cert.Thumbprint)\" -Force";

        return $"""
            $ErrorActionPreference = 'Stop'
            $cert = New-SelfSignedCertificate `
                -Type CodeSigningCert `
                -Subject 'CN={safeSubject}' `
                -KeyUsage DigitalSignature `
                -FriendlyName '{safeFriendly}' `
                -CertStoreLocation 'Cert:\CurrentUser\My'
            Write-Host "Certificate created (thumbprint: $($cert.Thumbprint))"
            $secPwd = ConvertTo-SecureString -String '{safePassword}' -Force -AsPlainText
            Export-PfxCertificate -Cert $cert -FilePath '{safeOutputPath}' -Password $secPwd | Out-Null
            Write-Host 'Certificate exported.'
            {removeStep}
            Write-Host 'Done.'
            """;
    }

    private static string Ps(string s) => s.Replace("'", "''").Replace("`", "``");
}
