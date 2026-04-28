namespace ForgeTekUpdatePackager.Models;

public class AppVersion
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string VersionNumber { get; set; } = string.Empty;
    public DateTime ScanDate { get; set; }
    public List<FileRecord> Files { get; set; } = [];

    public bool HasDiff { get; set; }
    public int AddedCount { get; set; }
    public int ModifiedCount { get; set; }
    public int RemovedCount { get; set; }

    public IEnumerable<FileRecord> NonDebugFiles => Files.Where(f => !f.IsDebug);
    public int TotalFiles => Files.Count;
    public int DebugFileCount => Files.Count(f => f.IsDebug);
    public int PackageFileCount => TotalFiles - DebugFileCount;
}
