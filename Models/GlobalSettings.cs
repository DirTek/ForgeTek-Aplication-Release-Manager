namespace ForgeTekUpdatePackager.Models;

public class GlobalSettings
{
    public string  RootFolder          { get; set; } = AppContext.BaseDirectory;
    public bool    UseGlobalCert       { get; set; } = false;
    public string? GlobalCertPath      { get; set; }
    public string? GlobalCertPassword  { get; set; }
}
