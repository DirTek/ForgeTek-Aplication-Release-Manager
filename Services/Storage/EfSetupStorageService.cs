using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ForgeTekApplicationReleaseManager.Data;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services.Security;

namespace ForgeTekApplicationReleaseManager.Services.Storage;

/// <summary>EF Core-backed setup-bundle store + "Past Bundles" history. Publish-profile secrets are
/// encrypted in the bundle's JSON payload (or dropped when the networked protector has no shared key yet).</summary>
public sealed class EfSetupStorageService(IDbContextFactory<ForgeTekDbContext> factory, ISecretProtector protector)
    : ISetupStorageService
{
    // Secret fields inside SetupBundle.PublishProfile (mirrors the file-based service).
    private static readonly string[] ProfileSecretKeys =
    [
        "ftpHost", "ftpUsername", "ftpPassword", "ftpRemotePath",
        "baseDownloadUrl", "sftpPassword", "s3SecretKey", "gitHubToken",
    ];

    // ── Bundles ──────────────────────────────────────────────────────────
    public IReadOnlyList<SetupBundle> GetAll()
    {
        using var db = factory.CreateDbContext();
        return db.SetupBundles.AsNoTracking().OrderBy(b => b.Name).Select(b => b.Payload).ToList()
            .Select(ReadBundle).ToList();
    }

    public SetupBundle? GetById(string id)
    {
        using var db = factory.CreateDbContext();
        var row = db.SetupBundles.AsNoTracking().FirstOrDefault(b => b.Id == id);
        return row is null ? null : ReadBundle(row.Payload);
    }

    public void Save(SetupBundle bundle)
    {
        var payload = WriteBundle(bundle);
        using var db = factory.CreateDbContext();
        var row = db.SetupBundles.FirstOrDefault(b => b.Id == bundle.Id);
        if (row is null)
            db.SetupBundles.Add(new SetupBundleRow { Id = bundle.Id, Name = bundle.Name, Payload = payload });
        else
        {
            row.Name = bundle.Name;
            row.Payload = payload;
            row.UpdatedUtc = DateTime.UtcNow;
        }
        db.SaveChanges();
    }

    public void Delete(string id)
    {
        using var db = factory.CreateDbContext();
        var row = db.SetupBundles.FirstOrDefault(b => b.Id == id);
        if (row is null) return;
        db.SetupBundles.Remove(row);
        db.SaveChanges();
    }

    // ── History ──────────────────────────────────────────────────────────
    public IReadOnlyList<GeneratedSetupRecord> GetHistory()
    {
        using var db = factory.CreateDbContext();
        return db.SetupHistory.AsNoTracking().OrderBy(h => h.GeneratedDate).Select(h => h.Payload).ToList()
            .Select(EfJson.Deserialize<GeneratedSetupRecord>).ToList();
    }

    public void AddHistory(GeneratedSetupRecord record)
    {
        using var db = factory.CreateDbContext();
        db.SetupHistory.Add(new SetupHistoryRow
        {
            Id = record.Id,
            BundleId = record.BundleId,
            GeneratedDate = record.GeneratedDate,
            Payload = EfJson.Serialize(record),
        });
        db.SaveChanges();
    }

    public void UpdateHistory(GeneratedSetupRecord record)
    {
        using var db = factory.CreateDbContext();
        var row = db.SetupHistory.FirstOrDefault(h => h.Id == record.Id);
        if (row is null)
            db.SetupHistory.Add(new SetupHistoryRow
            {
                Id = record.Id, BundleId = record.BundleId,
                GeneratedDate = record.GeneratedDate, Payload = EfJson.Serialize(record),
            });
        else
        {
            row.BundleId = record.BundleId;
            row.GeneratedDate = record.GeneratedDate;
            row.Payload = EfJson.Serialize(record);
        }
        db.SaveChanges();
    }

    public void ClearHistory()
    {
        using var db = factory.CreateDbContext();
        db.SetupHistory.RemoveRange(db.SetupHistory);
        db.SaveChanges();
    }

    // ── secret handling on the nested publish profile ────────────────────
    private string WriteBundle(SetupBundle bundle)
    {
        var root = JsonNode.Parse(EfJson.Serialize(bundle))!.AsObject();
        if (root["publishProfile"] is JsonObject profile)
            foreach (var key in ProfileSecretKeys) EfJson.ProtectOrDrop(profile, key, protector);
        return root.ToJsonString(EfJson.Options);
    }

    private SetupBundle ReadBundle(string payload)
    {
        var root = JsonNode.Parse(payload)!.AsObject();
        if (root["publishProfile"] is JsonObject profile)
            foreach (var key in ProfileSecretKeys) EfJson.Decrypt(profile, key, protector);
        return EfJson.Deserialize<SetupBundle>(root.ToJsonString(EfJson.Options));
    }
}
