namespace ForgeTekUpdatePackager.Models;

/// <summary>
/// A reusable preset of an app's non-secret pipeline settings (build runner, packaging, publishing).
/// Apply it to a new or existing app to avoid reconfiguring the same thing repeatedly. Secrets
/// (passwords, secret keys, tokens) are intentionally never stored in a template.
/// Null fields are left untouched when the template is applied.
/// </summary>
public class SettingsTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;

    /// <summary>True for shipped per-stack presets (not persisted, not deletable).</summary>
    public bool IsBuiltIn { get; set; }

    // ── Packaging ─────────────────────────────────────────────────────────
    public string? PackageExtension { get; set; }
    public string? PackageNameTemplate { get; set; }

    // ── Build runner ──────────────────────────────────────────────────────
    public string? GitHubBuildCommand { get; set; }
    public string? GitHubArtifactPath { get; set; }

    // ── Publishing (non-secret only) ───────────────────────────────────────
    public string? PublishProvider { get; set; }

    public string? FtpRemotePath { get; set; }
    public string? BaseDownloadUrl { get; set; }

    public string? SftpRemotePath { get; set; }
    public string? SftpBaseDownloadUrl { get; set; }

    public string? S3Endpoint { get; set; }
    public string? S3Region { get; set; }
    public string? S3Bucket { get; set; }
    public string? S3Prefix { get; set; }
    public string? S3PublicBaseUrl { get; set; }

    public string? GitHubReleaseTag { get; set; }
    public string? GitHubCatalogTag { get; set; }
}
