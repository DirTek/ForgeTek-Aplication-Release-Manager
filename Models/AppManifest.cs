namespace ForgeTekUpdatePackager.Models;

public class AppManifest
{
    public string Version { get; set; } = string.Empty;
    public string App { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
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
