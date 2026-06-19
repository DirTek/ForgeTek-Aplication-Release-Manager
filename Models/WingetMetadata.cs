namespace ForgeTekUpdatePackager.Models;

/// <summary>Per-app metadata used to generate a Windows Package Manager (winget) manifest.
/// All non-secret, persisted in <see cref="AppSettings"/>.</summary>
public class WingetMetadata
{
    /// <summary>"Publisher.Package" identifier. Blank → derived as "{Publisher}.{App}".</summary>
    public string? PackageIdentifier { get; set; }

    /// <summary>Short alias used for "winget install &lt;moniker&gt;". Blank → lowercased package name.</summary>
    public string? Moniker { get; set; }

    public string? ShortDescription { get; set; }
    public string? Description { get; set; }

    /// <summary>SPDX license identifier or name (e.g. "MIT").</summary>
    public string? License { get; set; }
    public string? LicenseUrl { get; set; }

    /// <summary>The app's home page (PackageUrl).</summary>
    public string? PackageUrl { get; set; }

    public List<string> Tags { get; set; } = [];

    /// <summary>Installer architecture: "x64" (default), "x86", "arm64", "neutral".</summary>
    public string Architecture { get; set; } = "x64";

    /// <summary>Installer type: "exe" (default), "inno", "nullsoft", "msi", "burn", etc.</summary>
    public string InstallerType { get; set; } = "exe";

    /// <summary>Explicit public download URL for the installer. Blank → suggested from the publish target.</summary>
    public string? InstallerUrl { get; set; }
}
