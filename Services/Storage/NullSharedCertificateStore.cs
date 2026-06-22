using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services.Storage;

/// <summary>Standalone no-op shared certificate store: nothing is shared (certs stay as local files).</summary>
public sealed class NullSharedCertificateStore : ISharedCertificateStore
{
    public bool IsShared => false;

    public Task<IReadOnlyList<SharedCertificate>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SharedCertificate>>([]);

    public Task<string> SaveAsync(string subject, string friendlyName, string thumbprint, byte[] pfx,
        string? byUser, CancellationToken ct = default) => Task.FromResult(string.Empty);

    public Task<byte[]?> GetPfxAsync(string id, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

    public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
}
