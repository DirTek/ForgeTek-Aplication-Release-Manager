using Microsoft.EntityFrameworkCore;
using ForgeTekApplicationReleaseManager.Data;
using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services.Storage;

/// <summary>EF Core-backed shared certificate store (networked mode). Stores the password-protected .pfx
/// and metadata; never the password.</summary>
public sealed class EfSharedCertificateStore(IDbContextFactory<ForgeTekDbContext> factory) : ISharedCertificateStore
{
    public bool IsShared => true;

    public async Task<IReadOnlyList<SharedCertificate>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Certificates.AsNoTracking()
            .OrderByDescending(c => c.CreatedUtc)
            .Select(c => new SharedCertificate(c.Id, c.Subject, c.FriendlyName, c.Thumbprint, c.CreatedUtc, c.CreatedBy))
            .ToListAsync(ct);
    }

    public async Task<string> SaveAsync(string subject, string friendlyName, string thumbprint, byte[] pfx,
        string? byUser, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString();
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Certificates.Add(new CertificateRow
        {
            Id = id,
            Subject = subject,
            FriendlyName = friendlyName,
            Thumbprint = thumbprint,
            Pfx = pfx,
            CreatedBy = byUser,
        });
        await db.SaveChangesAsync(ct);
        return id;
    }

    public async Task<byte[]?> GetPfxAsync(string id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Certificates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return row?.Pfx;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Certificates.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }
}
