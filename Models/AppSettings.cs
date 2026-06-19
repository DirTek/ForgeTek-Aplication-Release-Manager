namespace ForgeTekUpdatePackager.Models;

public class AppSettings
{
    public string? OutputFolder { get; set; }
    public string? DefaultCertPath { get; set; }
    public string? DefaultCertPassword { get; set; }

    /// <summary>Custom package file extension (without leading dot). Defaults to "ftu" when null or empty.</summary>
    public string? PackageExtension { get; set; }

    /// <summary>Optional package file name template with build variables (e.g. "{AppName}_{Version}_{Channel}").
    /// Blank uses the default "{AppName}-{Version}". The extension is added separately.</summary>
    public string? PackageNameTemplate { get; set; }

    /// <summary>Where releases are published: "Ftp" (default), "Sftp", "S3", or "GitHubReleases".</summary>
    public string? PublishProvider { get; set; }

    // FTP
    public string? FtpHost { get; set; }
    public int FtpPort { get; set; } = 21;
    public string? FtpUsername { get; set; }
    public string? FtpPassword { get; set; }

    /// <summary>Root path on the FTP server (e.g. "/public/releases").</summary>
    public string? FtpRemotePath { get; set; }

    /// <summary>Base HTTP URL for public downloads (e.g. "https://example.com/releases").</summary>
    public string? BaseDownloadUrl { get; set; }

    // ── SFTP (SSH file transfer) ──────────────────────────────────────────
    public string? SftpHost { get; set; }
    public int SftpPort { get; set; } = 22;
    public string? SftpUsername { get; set; }
    public string? SftpPassword { get; set; }
    /// <summary>Root path on the SFTP server.</summary>
    public string? SftpRemotePath { get; set; }
    /// <summary>Base HTTP URL the SFTP root is served from, for public downloads.</summary>
    public string? SftpBaseDownloadUrl { get; set; }

    // ── S3-compatible (AWS S3 / Cloudflare R2 / MinIO) ────────────────────
    /// <summary>Service endpoint URL (e.g. "https://&lt;account&gt;.r2.cloudflarestorage.com"). Empty = AWS.</summary>
    public string? S3Endpoint { get; set; }
    public string? S3Region { get; set; }
    public string? S3Bucket { get; set; }
    public string? S3AccessKey { get; set; }
    public string? S3SecretKey { get; set; }
    /// <summary>Optional key prefix within the bucket.</summary>
    public string? S3Prefix { get; set; }
    /// <summary>Public base URL objects are served from (CDN or bucket URL).</summary>
    public string? S3PublicBaseUrl { get; set; }

    // ── GitHub Releases ───────────────────────────────────────────────────
    /// <summary>Tag pattern for per-version package releases. Default "v{version}".</summary>
    public string? GitHubReleaseTag { get; set; }
    /// <summary>Fixed tag whose release hosts the update catalog (stable URL). Default "updates".</summary>
    public string? GitHubCatalogTag { get; set; }

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
