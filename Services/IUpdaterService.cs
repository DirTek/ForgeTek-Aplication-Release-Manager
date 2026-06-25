using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services;

/// <summary>Options for generating a per-app standalone updater EXE.</summary>
public sealed class UpdaterGenOptions
{
    /// <summary>The main application executable name the updater relaunches (e.g. "MyApp.exe").</summary>
    public required string AppExeName { get; init; }

    /// <summary>An <c>.ico</c> or an <c>.exe</c> to extract the branding icon from. Null = no baked icon.</summary>
    public string? IconSourcePath { get; init; }

    /// <summary>Destination folder for "{AppName}.Updater.exe" + updater.json.</summary>
    public required string OutputFolder { get; init; }

    /// <summary>Authenticode-sign the produced EXE when a global cert is configured.</summary>
    public bool Sign { get; init; }
}

public interface IUpdaterService
{
    /// <summary>
    /// Produces a standalone updater EXE for <paramref name="entry"/> in <c>options.OutputFolder</c>,
    /// plus an <c>updater.json</c> sidecar. Prefers compiling a branded copy on demand (icon baked in);
    /// falls back to the prebuilt generic updater + sidecar when the .NET SDK isn't available.
    /// </summary>
    Task<GeneratedUpdaterRecord> GenerateAsync(
        AppEntry entry,
        AppSettings settings,
        UpdaterGenOptions options,
        IProgress<string> progress,
        CancellationToken ct = default);
}
