using System.Text.Json.Serialization;

namespace SetupBootstrapper;

public class InstallManifest
{
    public string SetupName { get; set; } = string.Empty;
    public string SetupVersion { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string EulaText { get; set; } = string.Empty;
    public string? BannerImageName { get; set; }
    // App offered for launch on the final page (chosen in the bundle). Null = no launch option.
    public string? LaunchAppName { get; set; }
    public string? LaunchAppDir { get; set; }
    public string? LaunchExeName { get; set; }
    public List<InstallApp> Apps { get; set; } = [];
    public List<RedistInfo> Redists { get; set; } = [];
}

public class InstallApp
{
    public string Name { get; set; } = string.Empty;
    public string DefaultInstallDir { get; set; } = string.Empty;
    public string? LaunchExeName { get; set; }
    // The icon the user chose for this app (relative path within the app's files). Used for the
    // app's Control Panel DisplayIcon. Falls back to LaunchExeName when null.
    public string? IconFileName { get; set; }
    public List<RegistryEntryManifest> RegistryEntries { get; set; } = [];
}

public class RegistryEntryManifest
{
    public string Root { get; set; } = "HKCU";
    public string KeyPath { get; set; } = string.Empty;
    public string ValueName { get; set; } = string.Empty;
    public string ValueData { get; set; } = string.Empty;
    public string ValueKind { get; set; } = "String";
}
