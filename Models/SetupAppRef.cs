namespace ForgeTekApplicationReleaseManager.Models;

public enum VersionMode
{
    /// <summary>Include only the latest version's non-debug files.</summary>
    LatestOnly,

    /// <summary>Include all versions from initial → latest (cumulative merge, newest file wins).</summary>
    Cumulative,
}

public class SetupAppRef
{
    public string AppId { get; set; } = string.Empty;
    public VersionMode VersionMode { get; set; } = VersionMode.Cumulative;
    public string? LaunchExeName { get; set; }
    public string? SetupIconPath { get; set; }
    public bool CreateShortcut { get; set; } = true;
    /// <summary>When true, the end user can deselect this app during install. When false (default)
    /// the app is obligatory — shown checked and locked.</summary>
    public bool IsOptional { get; set; }
    /// <summary>For optional apps, whether the install-time checkbox starts checked.</summary>
    public bool DefaultSelected { get; set; } = true;
    /// <summary>Exe file names (within the app) to flag as "always run as administrator".</summary>
    public List<string> RunAsAdminExes { get; set; } = [];
    public List<RegistryEntry> RegistryEntries { get; set; } = [];
}
