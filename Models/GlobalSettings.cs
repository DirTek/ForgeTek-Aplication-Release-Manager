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
}
