namespace ForgeTekUpdatePackager.Models;

public class AppManifest
{
    public string Version { get; set; } = string.Empty;
    public string App { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>For a cumulative incremental, the full baseline this version's payload is relative to.</summary>
    public string? BaseVersion { get; set; }

    /// <summary>The full expected file set (every non-debug file + hash) the install should have after
    /// applying — lets the client verify completeness and self-heal even on a cumulative/skipped update.</summary>
    public List<ManifestFileEntry> Files { get; set; } = [];
    public List<string> RemovedFiles { get; set; } = [];
    public ManifestTotals Totals { get; set; } = new();
}

public class ManifestFileEntry
{
    public string Path { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public long Size { get; set; }
}

public class ManifestTotals
{
    public int FileCount { get; set; }
    public long TotalSize { get; set; }
}
