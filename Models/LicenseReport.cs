namespace ForgeTekApplicationReleaseManager.Models;

/// <summary>One third-party NuGet component and its license, as resolved from NuGet metadata.</summary>
public sealed record LicenseComponent(
    string Id,
    string Version,
    string License,        // SPDX expression / name, "Custom", or "Unknown"
    string LicenseUrl,
    string ProjectUrl,
    bool Transitive);

/// <summary>Result of a third-party license scan.</summary>
public sealed class LicenseReport
{
    public List<LicenseComponent> Components { get; init; } = [];

    /// <summary>Non-null when the scan could not run.</summary>
    public string? Error { get; init; }

    public string? ScannedPath { get; init; }

    public bool Ran => Error is null;
    public int Total => Components.Count;
}
