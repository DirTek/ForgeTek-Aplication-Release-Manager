namespace ForgeTekUpdatePackager.Models;

/// <summary>A standalone publish target (provider + credentials), independent of an app's update
/// settings. Used to publish a generated setup somewhere separate from the app's update feed — e.g.
/// updates on FTP but the installer on GitHub Releases. Field names mirror <see cref="AppSettings"/>
/// so it maps cleanly to the transport layer.</summary>
public class PublishProfile
{
    /// <summary>"Ftp" (default), "Sftp", "S3", or "GitHubReleases".</summary>
    public string? PublishProvider { get; set; }

    // FTP
    public string? FtpHost { get; set; }
    public int FtpPort { get; set; } = 21;
    public string? FtpUsername { get; set; }
    public string? FtpPassword { get; set; }
    public string? FtpRemotePath { get; set; }
    public string? BaseDownloadUrl { get; set; }

    // SFTP
    public string? SftpHost { get; set; }
    public int SftpPort { get; set; } = 22;
    public string? SftpUsername { get; set; }
    public string? SftpPassword { get; set; }
    public string? SftpRemotePath { get; set; }
    public string? SftpBaseDownloadUrl { get; set; }

    // S3-compatible
    public string? S3Endpoint { get; set; }
    public string? S3Region { get; set; }
    public string? S3Bucket { get; set; }
    public string? S3AccessKey { get; set; }
    public string? S3SecretKey { get; set; }
    public string? S3Prefix { get; set; }
    public string? S3PublicBaseUrl { get; set; }

    // GitHub Releases
    public string? GitHubRepo { get; set; }
    public string? GitHubToken { get; set; }
    public string? GitHubReleaseTag { get; set; }
    public string? GitHubCatalogTag { get; set; }

    /// <summary>Projects the profile onto an AppSettings so it can drive the existing publish transports.</summary>
    public AppSettings ToAppSettings() => new()
    {
        PublishProvider = PublishProvider,
        FtpHost = FtpHost, FtpPort = FtpPort, FtpUsername = FtpUsername, FtpPassword = FtpPassword,
        FtpRemotePath = FtpRemotePath, BaseDownloadUrl = BaseDownloadUrl,
        SftpHost = SftpHost, SftpPort = SftpPort, SftpUsername = SftpUsername, SftpPassword = SftpPassword,
        SftpRemotePath = SftpRemotePath, SftpBaseDownloadUrl = SftpBaseDownloadUrl,
        S3Endpoint = S3Endpoint, S3Region = S3Region, S3Bucket = S3Bucket, S3AccessKey = S3AccessKey,
        S3SecretKey = S3SecretKey, S3Prefix = S3Prefix, S3PublicBaseUrl = S3PublicBaseUrl,
        GitHubRepo = GitHubRepo, GitHubToken = GitHubToken,
        GitHubReleaseTag = GitHubReleaseTag, GitHubCatalogTag = GitHubCatalogTag,
    };

    /// <summary>Copies the publish fields out of an app's settings (for the "Copy from app" helper).</summary>
    public static PublishProfile FromAppSettings(AppSettings s) => new()
    {
        PublishProvider = s.PublishProvider,
        FtpHost = s.FtpHost, FtpPort = s.FtpPort, FtpUsername = s.FtpUsername, FtpPassword = s.FtpPassword,
        FtpRemotePath = s.FtpRemotePath, BaseDownloadUrl = s.BaseDownloadUrl,
        SftpHost = s.SftpHost, SftpPort = s.SftpPort, SftpUsername = s.SftpUsername, SftpPassword = s.SftpPassword,
        SftpRemotePath = s.SftpRemotePath, SftpBaseDownloadUrl = s.SftpBaseDownloadUrl,
        S3Endpoint = s.S3Endpoint, S3Region = s.S3Region, S3Bucket = s.S3Bucket, S3AccessKey = s.S3AccessKey,
        S3SecretKey = s.S3SecretKey, S3Prefix = s.S3Prefix, S3PublicBaseUrl = s.S3PublicBaseUrl,
        GitHubRepo = s.GitHubRepo, GitHubToken = s.GitHubToken,
        GitHubReleaseTag = s.GitHubReleaseTag, GitHubCatalogTag = s.GitHubCatalogTag,
    };

    /// <summary>True when the chosen provider has the minimum fields needed to publish.</summary>
    public bool IsConfigured()
    {
        return (PublishProvider switch
        {
            "Sftp" => !string.IsNullOrWhiteSpace(SftpHost),
            "S3" => !string.IsNullOrWhiteSpace(S3Bucket) && !string.IsNullOrWhiteSpace(S3AccessKey) && !string.IsNullOrWhiteSpace(S3SecretKey),
            "GitHubReleases" => !string.IsNullOrWhiteSpace(GitHubRepo),
            _ => !string.IsNullOrWhiteSpace(FtpHost),
        });
    }
}
