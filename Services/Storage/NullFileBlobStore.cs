using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services.Storage;

/// <summary>Standalone (non-networked) no-op blob store: nothing is stored and no blob is ever found, so
/// packaging falls back to reading source files from local disk exactly as before.</summary>
public sealed class NullFileBlobStore : IFileBlobStore
{
    public Task StoreAsync(string rootFolder, IEnumerable<FileRecord> files,
        IProgress<string>? progress = null, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> HasAsync(string sha, CancellationToken ct = default) => Task.FromResult(false);

    public Task<byte[]?> GetAsync(string sha, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

    public Task CollectGarbageAsync(CancellationToken ct = default) => Task.CompletedTask;
}
