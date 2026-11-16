using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ForgeTekApplicationReleaseManager.Data;

namespace ForgeTekApplicationReleaseManager.Services.Storage;

/// <summary>Takes a point-in-time copy of the database before a destructive action, so it can be undone.</summary>
public interface IDatabaseSnapshotService
{
    /// <summary>Writes a snapshot labelled with <paramref name="reason"/> and returns its path, or null when
    /// snapshots don't apply (networked SQL Server) or the snapshot failed (logged; never throws).</summary>
    string? TrySnapshot(string reason);
}

/// <summary>
/// Standalone (SQLite): <c>VACUUM INTO</c> a timestamped file under <c>data/snapshots</c> — a consistent copy
/// even while the app holds connections — keeping the most recent <see cref="Keep"/>. To restore, close FARM
/// and copy a snapshot over <c>data/forgetek.db</c>. Networked SQL Server backups are the server's job.
/// </summary>
public sealed class DatabaseSnapshotService(IDbContextFactory<ForgeTekDbContext> factory, ILogService log)
    : IDatabaseSnapshotService
{
    public const int Keep = 20;

    public string? TrySnapshot(string reason)
    {
        try
        {
            using var db = factory.CreateDbContext();
            if (!db.Database.IsSqlite()) return null;

            var dbPath = Path.GetFullPath(new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource);
            if (!File.Exists(dbPath)) return null;   // nothing to keep (and opening it would create an empty DB)
            var dir = Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "snapshots");
            Directory.CreateDirectory(dir);

            var label = new string(reason.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            if (label.Length > 60) label = label[..60];
            var path = Path.Combine(dir, $"forgetek-{DateTime.Now:yyyyMMdd-HHmmss}-{label}.db");
            if (File.Exists(path)) File.Delete(path);   // VACUUM INTO refuses an existing file

            db.Database.ExecuteSqlRaw("VACUUM INTO {0}", path);
            Prune(dir);
            log.Write("Snapshot", $"Database snapshot before {reason}: {path}");
            return path;
        }
        catch (Exception ex)
        {
            log.Write("Snapshot", $"Database snapshot before {reason} failed: {ex.Message}");
            return null;
        }
    }

    private static void Prune(string dir)
    {
        // Names start with a sortable timestamp, so name order is age order.
        foreach (var old in Directory.GetFiles(dir, "forgetek-*.db").OrderByDescending(f => f, StringComparer.Ordinal).Skip(Keep))
            try { File.Delete(old); } catch { /* in use or locked — try again next time */ }
    }
}
