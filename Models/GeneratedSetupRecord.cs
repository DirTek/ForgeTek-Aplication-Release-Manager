namespace ForgeTekUpdatePackager.Models;

/// <summary>One entry in the "Past Bundles" history — a record of a setup that was generated.</summary>
public class GeneratedSetupRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string BundleId { get; set; } = string.Empty;
    public string BundleName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime GeneratedDate { get; set; } = DateTime.Now;

    /// <summary>Full path of the generated Setup.exe.</summary>
    public string OutputPath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }

    /// <summary>When "Preserve old setups" was on and a prior file existed, the path it was renamed to.</summary>
    public string? ArchivedPath { get; set; }
}
