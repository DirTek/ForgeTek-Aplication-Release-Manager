using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ForgeTekUpdatePackager.Data;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services.Security;

namespace ForgeTekUpdatePackager.Services.Storage;

/// <summary>
/// EF Core-backed app store (replaces the per-file <see cref="StorageService"/> for SQLite/SQL Server).
/// Apps are cached in memory and served from the cache (each app carries ~2k file records, so re-querying
/// + re-deserializing on every call was a heavy per-call cost; the UI reads these liberally). This mirrors
/// the original file-based service, which also loaded once and mutated its in-memory list on writes. Writes
/// go to the DB with an optimistic-concurrency token and update the cache. Per-version FTP secrets are
/// encrypted in the JSON payload (or dropped when the networked protector has no shared key yet).
/// Multi-operator note: the cache is refreshed on writes and via <see cref="Refresh"/>; another operator's
/// changes appear after a refresh (same single-process staleness the file store had).
/// </summary>
public sealed class EfStorageService(IDbContextFactory<ForgeTekDbContext> factory, ISecretProtector protector)
    : IStorageService
{
    private readonly object _lock = new();
    private List<AppEntry>? _cache;

    private List<AppEntry> Cache()
    {
        lock (_lock)
        {
            if (_cache is null)
            {
                using var db = factory.CreateDbContext();
                _cache = db.Apps.AsNoTracking().OrderBy(a => a.Name).Select(r => r.Payload).ToList()
                    .Select(Read).ToList();
            }
            return _cache;
        }
    }

    /// <summary>Drops the in-memory cache so the next read reloads from the database.</summary>
    public void Refresh() { lock (_lock) _cache = null; }

    public IReadOnlyList<AppEntry> GetAll() { lock (_lock) return Cache().ToList(); }

    public AppEntry? GetById(string id) { lock (_lock) return Cache().FirstOrDefault(a => a.Id == id); }

    public void Add(AppEntry app)
    {
        using var db = factory.CreateDbContext();
        db.Apps.Add(new AppRow { Id = app.Id, Name = app.Name, Payload = Write(app) });
        db.SaveChanges();
        lock (_lock) { if (_cache is not null && _cache.All(a => a.Id != app.Id)) _cache.Add(app); }
    }

    public void Update(AppEntry app)
    {
        using var db = factory.CreateDbContext();
        var row = db.Apps.FirstOrDefault(a => a.Id == app.Id);
        if (row is null)
        {
            db.Apps.Add(new AppRow { Id = app.Id, Name = app.Name, Payload = Write(app) });
        }
        else
        {
            row.Name = app.Name;
            row.Payload = Write(app);
            row.UpdatedUtc = DateTime.UtcNow;
        }
        db.SaveChanges();

        // Keep the cache in sync with the saved entry (replace the instance for this id).
        lock (_lock)
        {
            if (_cache is not null)
            {
                _cache.RemoveAll(a => a.Id == app.Id);
                _cache.Add(app);
            }
        }
    }

    public void Delete(string id)
    {
        using var db = factory.CreateDbContext();
        var row = db.Apps.FirstOrDefault(a => a.Id == id);
        if (row is null) return;
        db.Apps.Remove(row);
        db.SaveChanges();
        lock (_lock) _cache?.RemoveAll(a => a.Id == id);
    }

    // Encrypt the per-version FTP secrets in the payload (same fields the file store protected).
    private string Write(AppEntry app)
    {
        var root = JsonNode.Parse(EfJson.Serialize(app))!.AsObject();
        if (root["versions"]?.AsArray() is { } versions)
            foreach (var item in versions)
                if (item is JsonObject v)
                {
                    EfJson.ProtectOrDrop(v, "ftpHost", protector);
                    EfJson.ProtectOrDrop(v, "ftpUsername", protector);
                    EfJson.ProtectOrDrop(v, "ftpPassword", protector);
                }
        return root.ToJsonString(EfJson.Options);
    }

    private AppEntry Read(string payload)
    {
        var root = JsonNode.Parse(payload)!.AsObject();
        if (root["versions"]?.AsArray() is { } versions)
            foreach (var item in versions)
                if (item is JsonObject v)
                {
                    EfJson.Decrypt(v, "ftpHost", protector);
                    EfJson.Decrypt(v, "ftpUsername", protector);
                    EfJson.Decrypt(v, "ftpPassword", protector);
                }
        return EfJson.Deserialize<AppEntry>(root.ToJsonString(EfJson.Options));
    }
}
