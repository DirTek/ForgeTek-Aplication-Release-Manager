using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services.Storage;

/// <summary>
/// Content-addressed store for version source files, used in networked mode so a version scanned on one
/// machine can be (re)packaged on another without the original local source folder. Blobs are keyed by
/// SHA-256 (<see cref="FileRecord.Checksum"/>), so identical files across versions/apps are stored once.
/// The standalone implementation (<see cref="NullFileBlobStore"/>) is a no-op, letting call sites stay
/// unconditional — packaging then simply reads from disk as it always has.
/// </summary>
public interface IFileBlobStore
{
    /// <summary>Persists the content of every shippable (non-debug) file under <paramref name="rootFolder"/>
    /// whose checksum is not already stored. Dedup is automatic, so the first version stores everything and
    /// later versions store only their changed files.</summary>
    Task StoreAsync(string rootFolder, IEnumerable<FileRecord> files,
        IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>True when a blob with this checksum is present.</summary>
    Task<bool> HasAsync(string sha, CancellationToken ct = default);

    /// <summary>Returns the original (decompressed) file bytes, or null when no blob with this checksum exists.</summary>
    Task<byte[]?> GetAsync(string sha, CancellationToken ct = default);

    /// <summary>Deletes blobs no longer referenced by any live (non-retracted/non-scrapped) version.</summary>
    Task CollectGarbageAsync(CancellationToken ct = default);
}
