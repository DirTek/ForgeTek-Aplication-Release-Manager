using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services.Storage;

/// <summary>
/// Stores generated code-signing certificates in the networked database so every operator can download or
/// register them locally. Only the password-protected .pfx and display metadata are kept — never the
/// password. The standalone implementation (<see cref="NullSharedCertificateStore"/>) is empty/no-op.
/// </summary>
public interface ISharedCertificateStore
{
    /// <summary>True when certificates are actually shared (networked). False standalone.</summary>
    bool IsShared { get; }

    /// <summary>Lists the shared certificates' metadata (no .pfx bytes).</summary>
    Task<IReadOnlyList<SharedCertificate>> ListAsync(CancellationToken ct = default);

    /// <summary>Persists a generated certificate's .pfx bytes + metadata. Returns its new id.</summary>
    Task<string> SaveAsync(string subject, string friendlyName, string thumbprint, byte[] pfx,
        string? byUser, CancellationToken ct = default);

    /// <summary>Returns the password-protected .pfx bytes for a stored certificate, or null if missing.</summary>
    Task<byte[]?> GetPfxAsync(string id, CancellationToken ct = default);

    /// <summary>Removes a shared certificate.</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);
}
