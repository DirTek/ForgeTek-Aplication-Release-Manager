namespace ForgeTekApplicationReleaseManager.Models;

public class GlobalSettings
{
    public string  RootFolder          { get; set; } = AppContext.BaseDirectory;
    public string  CompanyName         { get; set; } = string.Empty;
    public bool    UseGlobalCert       { get; set; } = false;
    public string? GlobalCertPath      { get; set; }
    public string? GlobalCertPassword  { get; set; }

    // Windows Certificate Store
    public bool    UseStoreCert        { get; set; } = false;
    public string? StoreCertThumbprint { get; set; }
    public bool    KeepInCertStore     { get; set; } = false;

    // UI appearance: "Dark" (default) or "Light".
    public string  Theme               { get; set; } = "Dark";

    /// <summary>Version-list channel filter, remembered across sessions: "All", "Stable", or "Beta".</summary>
    public string  VersionChannelFilter { get; set; } = "All";

    /// <summary>When on (and access protection is enabled), a release update or setup can't be published
    /// until an Admin and a QA Tester have approved it. Off by default so existing installs are unaffected.</summary>
    public bool    RequireReleaseApproval { get; set; } = false;

    // ── Publisher info (account-wide defaults for winget manifests) ────────
    /// <summary>Publisher home page (winget PublisherUrl).</summary>
    public string? PublisherUrl        { get; set; }
    /// <summary>Publisher support URL (winget PublisherSupportUrl).</summary>
    public string? PublisherSupportUrl { get; set; }

    /// <summary>Allow/warn/block thresholds for the dependency vulnerability gate.</summary>
    public VulnerabilityPolicy Vulnerability { get; set; } = new();

    /// <summary>Allow/warn/block lists for the third-party license gate.</summary>
    public LicensePolicy License { get; set; } = new();

    // ── GitHub account connection (OAuth device flow) ─────────────────────
    /// <summary>OAuth App Client ID (public) used for the device-flow sign-in.</summary>
    public string? GitHubClientId { get; set; }
    /// <summary>Account access token from the device flow (DPAPI-encrypted at rest).</summary>
    public string? GitHubToken    { get; set; }
    /// <summary>The connected account login (e.g. "octocat"), for display.</summary>
    public string? GitHubLogin    { get; set; }
}
