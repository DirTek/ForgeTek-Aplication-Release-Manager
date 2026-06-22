using Microsoft.EntityFrameworkCore;

namespace ForgeTekUpdatePackager.Data;

/// <summary>
/// EF Core context for the shared/local store. Holds the document-on-RDBMS rows (one JSON payload per
/// aggregate). The same model + migrations run on both SQLite (standalone) and SQL Server (networked) by
/// staying provider-neutral: a string concurrency token instead of SQL Server <c>rowversion</c>, and
/// <c>TEXT</c>/<c>nvarchar(max)</c> payload columns with no engine-specific SQL.
/// </summary>
public class ForgeTekDbContext(DbContextOptions<ForgeTekDbContext> options) : DbContext(options)
{
    public DbSet<AppRow> Apps => Set<AppRow>();
    public DbSet<AppSettingsRow> AppSettingsRows => Set<AppSettingsRow>();
    public DbSet<SetupBundleRow> SetupBundles => Set<SetupBundleRow>();
    public DbSet<SetupHistoryRow> SetupHistory => Set<SetupHistoryRow>();
    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<GlobalSettingsRow> GlobalSettingsRows => Set<GlobalSettingsRow>();
    public DbSet<ApprovalRow> Approvals => Set<ApprovalRow>();
    public DbSet<FileBlobRow> FileBlobs => Set<FileBlobRow>();
    public DbSet<CertificateRow> Certificates => Set<CertificateRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<ApprovalRow>().HasIndex(a => a.TargetKey);
        b.Entity<SetupHistoryRow>().HasIndex(h => h.BundleId);
    }

    public override int SaveChanges()
    {
        StampConcurrencyTokens();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        StampConcurrencyTokens();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    // Reassign a fresh token to every added/modified concurrency-stamped row so an optimistic-concurrency
    // check fires when two operators edit the same row (EF compares the original token to the DB value).
    private void StampConcurrencyTokens()
    {
        foreach (var entry in ChangeTracker.Entries<IConcurrencyStamped>())
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.RowVersion = Guid.NewGuid().ToString();
    }
}
