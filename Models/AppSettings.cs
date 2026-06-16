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

    // ── GitHub integration ────────────────────────────────────────────────
    /// <summary>Repository in "owner/name" form (e.g. "octocat/Hello-World").</summary>
    public string? GitHubRepo { get; set; }

    /// <summary>Personal access token (DPAPI-encrypted at rest). Optional for public repos.</summary>
    public string? GitHubToken { get; set; }

    /// <summary>Local working copy of the repo used by the build runner (git pull + build here).</summary>
    public string? GitHubLocalPath { get; set; }

    /// <summary>PowerShell build command run in the local path (e.g. "dotnet publish -c Release -o out").</summary>
    public string? GitHubBuildCommand { get; set; }

    /// <summary>Folder the build produces, scanned after a successful build.</summary>
    public string? GitHubArtifactPath { get; set; }
}
