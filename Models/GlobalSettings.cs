namespace ForgeTekUpdatePackager.Models;

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

    // ── GitHub account connection (OAuth device flow) ─────────────────────
    /// <summary>OAuth App Client ID (public) used for the device-flow sign-in.</summary>
    public string? GitHubClientId { get; set; }
    /// <summary>Account access token from the device flow (DPAPI-encrypted at rest).</summary>
    public string? GitHubToken    { get; set; }
    /// <summary>The connected account login (e.g. "octocat"), for display.</summary>
    public string? GitHubLogin    { get; set; }
}
