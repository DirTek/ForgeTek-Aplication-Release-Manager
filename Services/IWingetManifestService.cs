namespace ForgeTekApplicationReleaseManager.Services;

/// <summary>Inputs for a winget multi-file manifest. Pure data so generation is unit-testable.</summary>
public sealed record WingetManifestInput(
    string PackageIdentifier,
    string Version,
    string Publisher,
    string PackageName,
    string InstallerUrl,
    string InstallerSha256,
    string Architecture,
    string InstallerType,
    string? Moniker = null,
    string? ShortDescription = null,
    string? Description = null,
    string? License = null,
    string? LicenseUrl = null,
    string? PackageUrl = null,
    string? PublisherUrl = null,
    string? PublisherSupportUrl = null,
    IReadOnlyList<string>? Tags = null,
    string? SilentSwitch = null,
    string? SilentWithProgressSwitch = null,
    string DefaultLocale = "en-US");

public interface IWingetManifestService
{
    /// <summary>Builds the three winget YAML documents (version / installer / defaultLocale),
    /// keyed by file name. Pure — no I/O.</summary>
    IReadOnlyDictionary<string, string> BuildYaml(WingetManifestInput input);

    /// <summary>Writes the manifest under
    /// <c>{outputRoot}/manifests/{l}/{Publisher}/{Package}/{Version}/</c> and returns that folder.</summary>
    string Write(WingetManifestInput input, string outputRoot);

    /// <summary>Derives a "Publisher.Package" identifier from a company + app name (sanitized).</summary>
    string DeriveIdentifier(string publisher, string packageName);
}
