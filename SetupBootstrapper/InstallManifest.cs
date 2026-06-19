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

    // Window appearance.
    public string? BackgroundMode { get; set; }
    public string? BackgroundColor1 { get; set; }
    public string? BackgroundColor2 { get; set; }
    public string? BackgroundGradientDirection { get; set; }
    public string? BackgroundImageName { get; set; }
    public bool FixedSize { get; set; }
    public string? FooterWatermark { get; set; }
    public List<InstallApp> Apps { get; set; } = [];
    public List<RedistInfo> Redists { get; set; } = [];
    public List<InstallAction> PreActions { get; set; } = [];
    public List<InstallAction> PostActions { get; set; } = [];
}

/// <summary>A custom install step (service control, script, executable, file cleanup). Field names
/// mirror the generator's InstallActionManifest for camelCase JSON round-trip.</summary>
public class InstallAction
{
    public string Type { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string InlineScript { get; set; } = string.Empty;
    public string? StagedFileName { get; set; }
    public bool IgnoreFailure { get; set; }
    public int TimeoutSeconds { get; set; }
}

public class InstallApp
{
    public string Name { get; set; } = string.Empty;
    public string DefaultInstallDir { get; set; } = string.Empty;
    public string? LaunchExeName { get; set; }
    public bool CreateShortcut { get; set; }
    // The icon the user chose for this app (relative path within the app's files). Used for the
    // app's Control Panel DisplayIcon. Falls back to LaunchExeName when null.
    public string? IconFileName { get; set; }
    /// <summary>Exe file names to flag "always run as administrator" (AppCompatFlags Layers).</summary>
    public List<string> RunAsAdminExes { get; set; } = [];
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
