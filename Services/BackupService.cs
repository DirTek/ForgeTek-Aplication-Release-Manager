using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ForgeTekUpdatePackager.Data;

namespace ForgeTekUpdatePackager.Services;

/// <summary>
/// Backs up and restores the app's data. Since the move to EF Core the source of truth is the database
/// (SQLite standalone / SQL Server networked), so the backup is a provider-agnostic <b>logical export</b>:
/// every table is dumped into the zip (JSON for the document rows, binary entries for blobs/certs). Restore
/// re-imports those rows into whichever store is active — which also makes a SQLite backup restorable into a
/// SQL Server instance. Generated artifacts (releases/) and logs are still included from disk.
/// </summary>
public class BackupService(IDbContextFactory<ForgeTekDbContext> factory) : IBackupService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task CreateBackupAsync(
        string rootFolder,
        string globalSettingsFilePath,
        string outputPath,
        bool includeApps,
        bool includeSetups,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var tmpPath = outputPath + ".tmp";
        try
        {
            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(tmpPath, ZipArchiveMode.Create);

                // ── Database (the source of truth) ──────────────────────────────
                using var db = factory.CreateDbContext();

                WriteJson(zip, "db/Users.json", db.Users.AsNoTracking().ToList(), progress);
                WriteJson(zip, "db/GlobalSettings.json", db.GlobalSettingsRows.AsNoTracking().ToList(), progress);

                // Shared signing certificates (metadata + the password-protected .pfx bytes).
                var certs = db.Certificates.AsNoTracking()
                    .Select(c => new { c.Id, c.Subject, c.FriendlyName, c.Thumbprint, c.CreatedUtc, c.CreatedBy }).ToList();
                WriteJson(zip, "db/Certificates.json", certs, progress);
                foreach (var c in certs)
                {
                    ct.ThrowIfCancellationRequested();
                    var pfx = db.Certificates.AsNoTracking().Where(x => x.Id == c.Id).Select(x => x.Pfx).First();
                    WriteBytes(zip, $"db/certs/{c.Id}.pfx", pfx);
                }

                if (includeApps)
                {
                    WriteJson(zip, "db/Apps.json", db.Apps.AsNoTracking().ToList(), progress);
                    WriteJson(zip, "db/AppSettings.json", db.AppSettingsRows.AsNoTracking().ToList(), progress);
                    WriteJson(zip, "db/Approvals.json", db.Approvals.AsNoTracking().ToList(), progress);

                    // Source-file blobs: metadata as JSON, content as separate binary entries (avoids base64 bloat).
                    var blobs = db.FileBlobs.AsNoTracking()
                        .Select(b => new { b.Sha256, b.Length, b.Compressed, b.CreatedUtc }).ToList();
                    WriteJson(zip, "db/FileBlobs.json", blobs, progress);
                    foreach (var b in blobs)
                    {
                        ct.ThrowIfCancellationRequested();
                        var content = db.FileBlobs.AsNoTracking().Where(x => x.Sha256 == b.Sha256).Select(x => x.Content).First();
                        WriteBytes(zip, $"db/blobs/{b.Sha256}", content);
                    }
                }

                if (includeSetups)
                {
                    WriteJson(zip, "db/SetupBundles.json", db.SetupBundles.AsNoTracking().ToList(), progress);
                    WriteJson(zip, "db/SetupHistory.json", db.SetupHistory.AsNoTracking().ToList(), progress);
                }

                // ── On-disk artifacts + logs (not in the database) ──────────────
                if (includeApps)
                    AddFolder(zip, rootFolder, Path.Combine(rootFolder, "releases"), ct, progress);

                var certDir = Path.Combine(rootFolder, "Certificates");
                if (Directory.Exists(certDir))
                    foreach (var file in Directory.GetFiles(certDir, "*.pfx").Order())
                    {
                        ct.ThrowIfCancellationRequested();
                        var rel = Path.GetRelativePath(rootFolder, file).Replace('\\', '/');
                        zip.CreateEntryFromFile(file, rel);
                        progress.Report($"  + {rel}");
                    }

                var logDir = Path.Combine(rootFolder, "logs");
                if (Directory.Exists(logDir))
                    foreach (var file in Directory.GetFiles(logDir, "*.log").Order())
                    {
                        ct.ThrowIfCancellationRequested();
                        var rel = Path.GetRelativePath(rootFolder, file).Replace('\\', '/');
                        zip.CreateEntryFromFile(file, rel);
                        progress.Report($"  + {rel}");
                    }
            }, ct);

            File.Move(tmpPath, outputPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    /// <summary>Re-imports a logical-export backup into the active store (upsert by key). Returns the number
    /// of user rows restored, so callers (e.g. lockout recovery) can tell whether login can resume.</summary>
    public async Task<int> RestoreAsync(string zipPath, IProgress<string> progress, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(zipPath);
            using var db = factory.CreateDbContext();

            var users = RestoreTable<UserRow>(zip, "db/Users.json", db, r => [r.UsernameKey], progress);
            RestoreTable<GlobalSettingsRow>(zip, "db/GlobalSettings.json", db, r => [r.Id], progress);
            RestoreTable<AppRow>(zip, "db/Apps.json", db, r => [r.Id], progress);
            RestoreTable<AppSettingsRow>(zip, "db/AppSettings.json", db, r => [r.AppName], progress);
            RestoreTable<ApprovalRow>(zip, "db/Approvals.json", db, r => [r.Id], progress);
            RestoreTable<SetupBundleRow>(zip, "db/SetupBundles.json", db, r => [r.Id], progress);
            RestoreTable<SetupHistoryRow>(zip, "db/SetupHistory.json", db, r => [r.Id], progress);
            RestoreBlobs(zip, db, progress);
            RestoreCertificates(zip, db, progress);

            db.SaveChanges();
            return users;
        }, ct);
    }

    // ── Restore helpers ──────────────────────────────────────────────────────

    private static int RestoreTable<T>(ZipArchive zip, string entryName, ForgeTekDbContext db,
        Func<T, object[]> keyOf, IProgress<string> progress) where T : class
    {
        var rows = ReadJson<List<T>>(zip, entryName);
        if (rows is null) return 0;
        var set = db.Set<T>();
        foreach (var row in rows)
        {
            var existing = set.Find(keyOf(row));
            if (existing is null) set.Add(row);
            else db.Entry(existing).CurrentValues.SetValues(row);
        }
        progress.Report($"  ↺ {entryName} ({rows.Count})");
        return rows.Count;
    }

    private static void RestoreBlobs(ZipArchive zip, ForgeTekDbContext db, IProgress<string> progress)
    {
        var meta = ReadJson<List<BlobMeta>>(zip, "db/FileBlobs.json");
        if (meta is null) return;
        foreach (var m in meta)
        {
            var content = ReadBytes(zip, $"db/blobs/{m.Sha256}");
            if (content is null) continue;
            var existing = db.FileBlobs.Find(m.Sha256);
            if (existing is null)
                db.FileBlobs.Add(new FileBlobRow { Sha256 = m.Sha256, Length = m.Length, Compressed = m.Compressed, Content = content, CreatedUtc = m.CreatedUtc });
            else { existing.Length = m.Length; existing.Compressed = m.Compressed; existing.Content = content; }
        }
        progress.Report($"  ↺ db/FileBlobs.json ({meta.Count})");
    }

    private static void RestoreCertificates(ZipArchive zip, ForgeTekDbContext db, IProgress<string> progress)
    {
        var meta = ReadJson<List<CertMeta>>(zip, "db/Certificates.json");
        if (meta is null) return;
        foreach (var c in meta)
        {
            var pfx = ReadBytes(zip, $"db/certs/{c.Id}.pfx");
            if (pfx is null) continue;
            var existing = db.Certificates.Find(c.Id);
            if (existing is null)
                db.Certificates.Add(new CertificateRow { Id = c.Id, Subject = c.Subject, FriendlyName = c.FriendlyName, Thumbprint = c.Thumbprint, Pfx = pfx, CreatedUtc = c.CreatedUtc, CreatedBy = c.CreatedBy });
            else { existing.Subject = c.Subject; existing.FriendlyName = c.FriendlyName; existing.Thumbprint = c.Thumbprint; existing.Pfx = pfx; existing.CreatedUtc = c.CreatedUtc; existing.CreatedBy = c.CreatedBy; }
        }
        progress.Report($"  ↺ db/Certificates.json ({meta.Count})");
    }

    private sealed record BlobMeta(string Sha256, long Length, bool Compressed, DateTime CreatedUtc);
    private sealed record CertMeta(string Id, string Subject, string FriendlyName, string Thumbprint,
        DateTime CreatedUtc, string? CreatedBy);

    // ── Zip I/O helpers ──────────────────────────────────────────────────────

    private static void WriteJson<T>(ZipArchive zip, string entryName, T value, IProgress<string> progress)
    {
        using var stream = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
        JsonSerializer.Serialize(stream, value, Json);
        progress.Report($"  + {entryName}");
    }

    private static void WriteBytes(ZipArchive zip, string entryName, byte[] bytes)
    {
        using var stream = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static T? ReadJson<T>(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName);
        if (entry is null) return default;
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, Json);
    }

    private static byte[]? ReadBytes(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName);
        if (entry is null) return null;
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static void AddFolder(ZipArchive zip, string rootFolder, string folder,
        CancellationToken ct, IProgress<string> progress)
    {
        if (!Directory.Exists(folder)) return;

        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order())
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(rootFolder, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, rel);
            progress.Report($"  + {rel}");
        }
    }
}
