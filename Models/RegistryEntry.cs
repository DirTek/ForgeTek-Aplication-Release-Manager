namespace ForgeTekApplicationReleaseManager.Models;

public class RegistryEntry
{
    public string Root { get; set; } = "HKCU";
    public string KeyPath { get; set; } = string.Empty;
    public string ValueName { get; set; } = string.Empty;
    public string ValueData { get; set; } = string.Empty;
    public string ValueKind { get; set; } = "String";
}
