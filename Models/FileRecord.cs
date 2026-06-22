namespace ForgeTekApplicationReleaseManager.Models;

public class FileRecord
{
    public string Path { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
    public DateTime DateModified { get; set; }

    /// <summary>Excluded from the package but kept on the client (e.g. a live self-updater). Not shipped, not deleted.</summary>
    public bool IsDebug { get; set; }

    /// <summary>Explicitly marked for deletion on clients — not shipped, and added to the package's
    /// RemovedFiles so the updater deletes it. Distinct from <see cref="IsDebug"/> (which keeps the file).</summary>
    public bool IsRemoved { get; set; }
}
