namespace ForgeTekApplicationReleaseManager.Models;

/// <summary>One entry in the "Past Bundles" history — a record of a setup that was generated.</summary>
public class GeneratedSetupRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string BundleId { get; set; } = string.Empty;
    public string BundleName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime GeneratedDate { get; set; } = DateTime.Now;

    /// <summary>Who generated it — the signed-in user, or the Windows user when unprotected.</summary>
    public string? GeneratedBy { get; set; }

    /// <summary>Full path of the generated Setup.exe.</summary>
    public string OutputPath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }

    /// <summary>SHA256 of the generated Setup.exe (uppercase hex, winget convention). Null for
    /// records generated before hashing was added — recomputed on demand from OutputPath.</summary>
    public string? Sha256 { get; set; }

    /// <summary>Public URL the setup was published to (FTP/SFTP/S3/GitHub Releases), once uploaded.</summary>
    public string? PublishedUrl { get; set; }
    /// <summary>The provider the setup was published with (e.g. "SFTP"), for display.</summary>
    public string? PublishedProvider { get; set; }
    /// <summary>When the setup was last published. Null until published; cleared on unpublish.</summary>
    public DateTime? PublishedDate { get; set; }

    /// <summary>When "Preserve old setups" was on and a prior file existed, the path it was renamed to.</summary>
    public string? ArchivedPath { get; set; }

    /// <summary>App version numbers captured at generation time (AppId → version), so each app's
    /// version history can show when a setup that shipped that version was generated.</summary>
    public Dictionary<string, string> AppVersions { get; set; } = [];
}
