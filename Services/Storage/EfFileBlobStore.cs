using System.IO;
using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using ForgeTekApplicationReleaseManager.Data;
using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services.Storage;

/// <summary>
/// EF Core-backed content-addressed blob store (networked mode). Stores shippable source files keyed by
/// their SHA-256, GZip-compressed when that actually shrinks them. Dedup is the primary key on
/// <see cref="FileBlobRow.Sha256"/>, so the first version stores everything and later versions add only
/// their changed files. <see cref="CollectGarbageAsync"/> drops blobs no longer referenced by a live
/// version, keyed off the same store the rest of the app reads.
/// </summary>
public sealed class EfFileBlobStore(IDbContextFactory<ForgeTekDbContext> factory, IStorageService storage)
    : IFileBlobStore
{
    public async Task StoreAsync(string rootFolder, IEnumerable<FileRecord> files,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // Only shippable files need to be reproducible elsewhere; debug/removed ones are never packaged.
        var shippable = files
            .Where(f => !f.IsDebug && !f.IsRemoved && !string.IsNullOrWhiteSpace(f.Checksum))
            .GroupBy(f => f.Checksum, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (shippable.Count == 0) return;

        await using var db = await factory.CreateDbContextAsync(ct);

        // Skip checksums already present (dedup across versions and apps).
        var wanted = shippable.Select(f => f.Checksum).ToList();
        var existing = await db.FileBlobs
            .Where(b => wanted.Contains(b.Sha256))
            .Select(b => b.Sha256)
            .ToListAsync(ct);
        var have = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var f in shippable)
        {
            ct.ThrowIfCancellationRequested();
            if (have.Contains(f.Checksum)) continue;

            var fullPath = Path.Combine(rootFolder, f.Path);
            if (!File.Exists(fullPath))
            {
                progress?.Report($"  ⚠ {f.Path} — not found on disk, skipped");
                continue;
            }

            var raw = await File.ReadAllBytesAsync(fullPath, ct);
            var (content, compressed) = Pack(raw);
            db.FileBlobs.Add(new FileBlobRow
            {
                Sha256 = f.Checksum.ToLowerInvariant(),
                Length = raw.LongLength,
                Compressed = compressed,
                Content = content,
            });
            have.Add(f.Checksum);   // guard against duplicate paths with same content within this batch
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            progress?.Report($"  Stored {added} new file blob(s) in the database.");
        }
    }

    public async Task<bool> HasAsync(string sha, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sha)) return false;
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.FileBlobs.AnyAsync(b => b.Sha256 == sha, ct);
    }

    public async Task<byte[]?> GetAsync(string sha, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sha)) return null;
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.FileBlobs.AsNoTracking().FirstOrDefaultAsync(b => b.Sha256 == sha, ct);
        if (row is null) return null;
        return row.Compressed ? Unpack(row.Content, row.Length) : row.Content;
    }

    public async Task CollectGarbageAsync(CancellationToken ct = default)
    {
        // Live = every non-debug file of a version that hasn't been retracted or scrapped, across all apps.
        var referenced = storage.GetAll()
            .SelectMany(a => a.Versions
                .Where(v => v.Status != VersionStatus.Retracted && v.Status != VersionStatus.Scrapped)
                .SelectMany(v => v.NonDebugFiles))
            .Select(f => f.Checksum)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await using var db = await factory.CreateDbContextAsync(ct);
        var allShas = await db.FileBlobs.Select(b => b.Sha256).ToListAsync(ct);
        var orphans = allShas.Where(s => !referenced.Contains(s)).ToList();
        if (orphans.Count == 0) return;

        // Delete in batches to keep parameter counts sane on SQL Server.
        foreach (var chunk in orphans.Chunk(200))
        {
            ct.ThrowIfCancellationRequested();
            await db.FileBlobs.Where(b => chunk.Contains(b.Sha256)).ExecuteDeleteAsync(ct);
        }
    }

    // GZip the bytes; keep them raw if compression doesn't help (already-compressed assets).
    private static (byte[] content, bool compressed) Pack(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw, 0, raw.Length);
        var packed = ms.ToArray();
        return packed.Length < raw.Length ? (packed, true) : (raw, false);
    }

    private static byte[] Unpack(byte[] packed, long originalLength)
    {
        using var src = new MemoryStream(packed);
        using var gz = new GZipStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream(originalLength > 0 ? (int)Math.Min(originalLength, int.MaxValue) : 0);
        gz.CopyTo(dst);
        return dst.ToArray();
    }
}
