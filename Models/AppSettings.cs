namespace ForgeTekUpdatePackager.Models;

public class AppSettings
{
    public string? OutputFolder { get; set; }
    public string? DefaultCertPath { get; set; }
    public string? DefaultCertPassword { get; set; }

    /// <summary>Custom package file extension (without leading dot). Defaults to "ftu" when null or empty.</summary>
    public string? PackageExtension { get; set; }

    // FTP
    public string? FtpHost { get; set; }
    public int FtpPort { get; set; } = 21;
    public string? FtpUsername { get; set; }
    public string? FtpPassword { get; set; }

    /// <summary>Root path on the FTP server (e.g. "/public/releases").</summary>
    public string? FtpRemotePath { get; set; }

    /// <summary>Base HTTP URL for public downloads (e.g. "https://example.com/releases").</summary>
    public string? BaseDownloadUrl { get; set; }

    // Windows Certificate Store
    public bool    UseStoreCert        { get; set; } = false;
    public string? StoreCertThumbprint { get; set; }
}
