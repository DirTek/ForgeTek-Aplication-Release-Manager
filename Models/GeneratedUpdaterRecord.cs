namespace ForgeTekApplicationReleaseManager.Models;

/// <summary>Result of generating a per-app standalone updater EXE.</summary>
public class GeneratedUpdaterRecord
{
    public string AppName { get; set; } = string.Empty;

    /// <summary>Full path of the generated "{AppName}.Updater.exe" (config is embedded inside it).</summary>
    public string OutputPath { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    /// <summary>SHA256 of the generated EXE (lowercase hex). Null if hashing failed.</summary>
    public string? Sha256 { get; set; }

    public DateTime GeneratedDate { get; set; } = DateTime.Now;

    /// <summary>True when the EXE was compiled fresh with the app icon baked in; false when the
    /// prebuilt fallback was copied (branding still applied at runtime from the sidecar).</summary>
    public bool Branded { get; set; }
}
