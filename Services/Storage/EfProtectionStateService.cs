using Microsoft.EntityFrameworkCore;
using ForgeTekUpdatePackager.Data;

namespace ForgeTekUpdatePackager.Services.Storage;

/// <summary>
/// DB-backed protection marker for the networked (shared) deployment, so "access protection is enabled" is
/// consistent for every operator instead of living in each machine's registry. Stored as a flag on the
/// single global-settings row.
/// </summary>
public sealed class EfProtectionStateService(IDbContextFactory<ForgeTekDbContext> factory) : IProtectionStateService
{
    public bool IsMarked
    {
        get
        {
            using var db = factory.CreateDbContext();
            return db.GlobalSettingsRows.AsNoTracking()
                .Any(g => g.Id == GlobalSettingsRow.SingletonId && g.ProtectionEnabled);
        }
    }

    public void Mark() => Set(true);
    public void Clear() => Set(false);

    private void Set(bool enabled)
    {
        using var db = factory.CreateDbContext();
        var row = db.GlobalSettingsRows.FirstOrDefault(g => g.Id == GlobalSettingsRow.SingletonId);
        if (row is null)
            db.GlobalSettingsRows.Add(new GlobalSettingsRow { ProtectionEnabled = enabled, Payload = "{}" });
        else
            row.ProtectionEnabled = enabled;
        db.SaveChanges();
    }
}
